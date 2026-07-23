using System.Text;
using Turso.Core.Parsing;
using Turso.Core.Storage;

namespace Turso.Core;

/// <summary>
/// The catalog reconstructed from a managed file-backed database.
/// </summary>
internal sealed record EmbeddedFileCatalog(
    Dictionary<string, EmbeddedTable> Tables,
    Dictionary<string, ViewDefinition> Views,
    Dictionary<string, TriggerDefinition> Triggers);

/// <summary>
/// Bridges the managed <see cref="EmbeddedDatabase"/> catalog to durable,
/// SQLite-format storage. It persists the schema on page 1 as a real
/// <c>sqlite_schema</c> table b-tree and stores each ordinary user table's
/// rows and BINARY ascending secondary indexes in recursively constructed
/// SQLite b-trees. The supported WITHOUT ROWID subset uses recursively
/// constructed SQLite index b-trees. Table and index records may use standard
/// SQLite overflow pages. All bytes are genuine SQLite page, cell, and record encodings;
/// nothing is a bespoke serialization format.
/// </summary>
/// <remarks>
/// This is a deliberately limited, honest engine. It only accepts schema and
/// data it can represent losslessly in real SQLite format and rejects everything
/// else up front so a persisted file stays a valid SQLite database. See the
/// reject rules in <see cref="ValidateTableRepresentable"/> and the documented
/// gaps on <c>OpenManagedDatabase</c>.
/// </remarks>
internal sealed class EmbeddedFileStore : IDisposable
{
    // sqlite_schema is: (type, name, tbl_name, rootpage, sql).
    private const int SchemaColumnCount = 5;
    private const uint SchemaRootPage = 1;

    private readonly IFileSystem _fileSystem;
    private readonly string _databasePath;
    private readonly string _walPath;
    private readonly int _pageSize;
    private readonly int _usableSpace;
    private SqliteDatabaseHeader _header;
    private readonly SqliteTextEncoding _textEncoding;

    private readonly SqlitePager _pager;
    private Dictionary<string, uint> _tableRootPages = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, uint> _indexRootPages = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSchemaSignature = string.Empty;
    private Exception? _postCommitMaintenanceFailure;
    private bool _disposed;

    private EmbeddedFileStore(IFileSystem fileSystem, string databasePath, string walPath, SqlitePager pager, SqliteDatabaseHeader header)
    {
        _fileSystem = fileSystem;
        _databasePath = databasePath;
        _walPath = walPath;
        _pager = pager;
        _header = header;
        _pageSize = header.PageSize;
        _usableSpace = header.UsableSpace;
        _textEncoding = header.TextEncoding == SqliteTextEncoding.Unset
            ? SqliteTextEncoding.Utf8
            : header.TextEncoding;
    }

    /// <summary>
    /// Opens (or creates) the managed file database and reconstructs its catalog
    /// from the committed SQLite pages.
    /// </summary>
    public static EmbeddedFileStore Open(
        string path,
        IFileSystem fileSystem,
        out EmbeddedFileCatalog catalog,
        TursoEncryptionOptions? encryption = null,
        bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var walPath = path + "-wal";
        var databaseExists = fileSystem.FileExists(path);
        var walExists = fileSystem.FileExists(walPath);

        SqlitePager pager;
        if (!databaseExists && !walExists)
        {
            if (readOnly)
            {
                throw new EmbeddedSqlException(
                    $"Cannot open managed database '{path}' read-only because neither its database file nor write-ahead log exists.");
            }

            var header = SqliteDatabaseHeader.CreateDefault();
            var walHeader = SqliteWalHeader.Create(
                header.PageSize,
                unchecked((uint)Random.Shared.Next()),
                unchecked((uint)Random.Shared.Next()));
            pager = SqlitePager.Create(fileSystem, path, walPath, walHeader, header, encryption: encryption);
        }
        else if (databaseExists && walExists)
        {
            pager = SqlitePager.Open(fileSystem, path, walPath, readOnly, encryption: encryption);
        }
        else
        {
            if (readOnly)
            {
                throw new EmbeddedSqlException(
                    $"Cannot open managed database '{path}' read-only because its database file and write-ahead log must both exist. "
                    + "Creating or recovering the missing companion file would mutate storage.");
            }

            throw new EmbeddedSqlException(
                $"The managed file database '{path}' is missing its companion write-ahead log; the managed file engine only reopens databases it created.");
        }

        try
        {
            var header = SqliteDatabaseHeader.Parse(pager.ReadCommittedPage(SchemaRootPage));
            var store = new EmbeddedFileStore(fileSystem, path, walPath, pager, header);
            catalog = store.Load();
            return store;
        }
        catch
        {
            pager.Dispose();
            throw;
        }
    }

    private EmbeddedFileCatalog Load()
    {
        var tables = new Dictionary<string, EmbeddedTable>(StringComparer.OrdinalIgnoreCase);
        var views = new Dictionary<string, ViewDefinition>(StringComparer.OrdinalIgnoreCase);
        var triggers = new Dictionary<string, TriggerDefinition>(StringComparer.OrdinalIgnoreCase);
        var rootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var indexRootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        var schemaEntries = ReadSchemaEntries();
        ValidateSchemaEntries(schemaEntries);

        var occupiedBtreePages = new HashSet<uint>(
            schemaEntries
                .Where(entry => entry.Type is "table" or "index")
                .Select(entry => entry.RootPage));

        // Materialize tables first so views and triggers can be parsed afterwards.
        foreach (var entry in schemaEntries)
        {
            if (!string.Equals(entry.Type, "table", StringComparison.Ordinal))
                continue;

            var statement = SqlParser.Parse(entry.Sql, SqlParameterMap.Parse(entry.Sql));
            if (statement is not CreateTableStatement create)
                throw new EmbeddedSqlException($"Stored schema for table '{entry.Name}' is not a CREATE TABLE statement.");
            if (!string.Equals(create.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"Stored schema entry for table '{entry.Name}' does not match its CREATE TABLE name.");
            }
            var table = new EmbeddedTable(create.Columns, create.WithoutRowid, create.PrimaryKeyColumns);
            LoadTableRows(entry.Name, table, entry.RootPage, occupiedBtreePages);
            tables[entry.Name] = table;
            rootPages[entry.Name] = entry.RootPage;
        }

        foreach (var entry in schemaEntries)
        {
            if (!string.Equals(entry.Type, "index", StringComparison.Ordinal))
                continue;

            if (!tables.TryGetValue(entry.TableName, out var table))
            {
                throw new EmbeddedSqlException(
                    $"Stored index '{entry.Name}' references missing table '{entry.TableName}'.");
            }
            if (table.WithoutRowid)
            {
                throw new EmbeddedSqlException(
                    $"Stored index '{entry.Name}' references WITHOUT ROWID table '{entry.TableName}', but secondary indexes on the managed WITHOUT ROWID persistence subset are not supported.");
            }

            var statement = SqlParser.Parse(entry.Sql, SqlParameterMap.Parse(entry.Sql));
            if (statement is not CreateIndexStatement create)
                throw new EmbeddedSqlException($"Stored schema for index '{entry.Name}' is not a CREATE INDEX statement.");
            if (!string.Equals(create.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(create.TableName, entry.TableName, StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"Stored schema entry for index '{entry.Name}' does not match its sqlite_schema name or table.");
            }

            var index = CreateIndexDefinition(entry.TableName, table, create);
            ValidateIndexRepresentable(entry.TableName, table, index);
            ValidateStoredIndex(entry, table, index, occupiedBtreePages);
            table.Indexes.Add(index);
            indexRootPages.Add(entry.Name, entry.RootPage);
        }

        ValidateAllocationMap(schemaEntries, tables);

        foreach (var entry in schemaEntries)
        {
            var statement = SqlParser.Parse(entry.Sql, SqlParameterMap.Parse(entry.Sql));
            switch (entry.Type)
            {
                case "table":
                case "index":
                    continue;
                case "view" when statement is CreateViewStatement view:
                    ValidateStoredView(entry, view);
                    views[entry.Name] = new ViewDefinition(view.Name, view.Columns, view.Query, view.Sql);
                    break;
                case "trigger" when statement is CreateTriggerStatement trigger:
                    ValidateStoredTrigger(entry, trigger, tables);
                    triggers[entry.Name] = new TriggerDefinition(
                        trigger.Name,
                        trigger.Event,
                        trigger.TableName,
                        trigger.Body,
                        trigger.Sql);
                    break;
                default:
                    throw new EmbeddedSqlException($"Stored schema entry '{entry.Name}' has an unsupported type '{entry.Type}'.");
            }
        }

        _tableRootPages = rootPages;
        _indexRootPages = indexRootPages;
        _lastSchemaSignature = ComputeSchemaSignature(schemaEntries);
        return new EmbeddedFileCatalog(tables, views, triggers);
    }

    private void ValidateAllocationMap(
        IReadOnlyList<SchemaEntry> schemaEntries,
        IReadOnlyDictionary<string, EmbeddedTable> tables)
    {
        try
        {
            var pageCount = _pager.CommittedPageCount;
            var freelist = SqliteFreelist.Read(
                _header,
                pageCount,
                _pager.ReadCommittedPage);
            var activePages = new HashSet<uint>();
            var overflowReader = new SqliteOverflowChainReader(_pager, _header);

            AddOwnedPage(activePages, SchemaRootPage, pageCount, "sqlite_schema");
            _ = CollectTableTreeNodePages(
                "sqlite_schema",
                SchemaRootPage,
                _pager.ReadCommittedPage(SchemaRootPage),
                activePages,
                pageCount,
                overflowReader,
                "root",
                isFirstPage: true);

            foreach (var entry in schemaEntries)
            {
                switch (entry.Type)
                {
                    case "table":
                        if (!tables.TryGetValue(entry.Name, out var table))
                        {
                            throw new InvalidDataException(
                                $"Managed file database is missing the loaded definition for table '{entry.Name}'.");
                        }
                        CollectTableTreePages(entry, table, activePages, pageCount, overflowReader);
                        break;
                    case "index":
                        CollectIndexTreePages(entry, activePages, pageCount, overflowReader);
                        break;
                }
            }

            foreach (var activePage in activePages)
            {
                if (freelist.PageNumbers.Contains(activePage))
                {
                    throw new InvalidDataException(
                        $"SQLite page {activePage} is both reachable and present in the freelist.");
                }
            }

            var accountedPageCount = checked(activePages.Count + freelist.PageNumbers.Count);
            if (accountedPageCount != pageCount)
            {
                throw new InvalidDataException(
                    $"SQLite allocation map accounts for {accountedPageCount} page(s), but the database has {pageCount}.");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new EmbeddedSqlException(
                "Managed file database has an invalid SQLite page allocation map.",
                exception);
        }
    }

    private void CollectTableTreePages(
        SchemaEntry entry,
        EmbeddedTable table,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader)
    {
        if (table.WithoutRowid)
        {
            CollectWithoutRowidTableTreePages(entry, activePages, pageCount, overflowReader);
            return;
        }

        AddOwnedPage(activePages, entry.RootPage, pageCount, $"table '{entry.Name}' root");
        _ = CollectTableTreeNodePages(
            entry.Name,
            entry.RootPage,
            _pager.ReadCommittedPage(entry.RootPage),
            activePages,
            pageCount,
            overflowReader,
            "root");
    }

    private int CollectTableTreeNodePages(
        string tableName,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner,
        bool isFirstPage = false)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage, isFirstPage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                CollectTableLeafOverflowPages(
                    SqliteTableLeafPageView.Parse(pageImage, _usableSpace, isFirstPage),
                    activePages,
                    pageCount,
                    overflowReader,
                    $"table '{tableName}' {owner}");
                return 0;
            case SqliteBtreePageType.TableInterior:
                {
                    var interior = SqliteTableInteriorPageView.Parse(pageImage, _usableSpace, isFirstPage);
                    int? childHeight = null;
                    foreach (var childPage in interior.Cells
                                 .Select(cell => cell.Cell.LeftChildPage)
                                 .Append(interior.Header.RightMostChildPage))
                    {
                        AddOwnedPage(
                            activePages,
                            childPage,
                            pageCount,
                            $"table '{tableName}' interior child {pageNumber}");
                        var height = CollectTableTreeNodePages(
                            tableName,
                            childPage,
                            _pager.ReadCommittedPage(childPage),
                            activePages,
                            pageCount,
                            overflowReader,
                            $"interior child {pageNumber}");
                        if (childHeight is { } expectedHeight && height != expectedHeight)
                        {
                            throw new InvalidDataException(
                                $"Stored table '{tableName}' interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
                        }

                        childHeight = height;
                    }

                    return checked((childHeight ?? throw new InvalidDataException(
                        $"Stored table '{tableName}' has an empty interior page {pageNumber}.")) + 1);
                }
            default:
                throw new InvalidDataException(
                    $"Stored table '{tableName}' {owner} page {pageNumber} has unsupported type {header.PageType}.");
        }
    }

    private void CollectWithoutRowidTableTreePages(
        SchemaEntry entry,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader)
    {
        AddOwnedPage(activePages, entry.RootPage, pageCount, $"WITHOUT ROWID table '{entry.Name}' root");
        var rootPage = _pager.ReadCommittedPage(entry.RootPage);
        var rootHeader = SqliteBtreePageHeader.Parse(rootPage);
        if (rootHeader.PageType is not (SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.IndexInterior))
        {
            throw new InvalidDataException(
                $"Stored WITHOUT ROWID table '{entry.Name}' root page has unsupported type {rootHeader.PageType}.");
        }

        _ = CollectIndexTreeNodePages(
            $"WITHOUT ROWID table '{entry.Name}'",
            entry.RootPage,
            rootPage,
            activePages,
            pageCount,
            overflowReader,
            "root");
    }

    private void CollectIndexTreePages(
        SchemaEntry entry,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader)
    {
        AddOwnedPage(activePages, entry.RootPage, pageCount, $"index '{entry.Name}' root");
        _ = CollectIndexTreeNodePages(
            $"index '{entry.Name}'",
            entry.RootPage,
            _pager.ReadCommittedPage(entry.RootPage),
            activePages,
            pageCount,
            overflowReader,
            "root");
    }

    private int CollectIndexTreeNodePages(
        string treeDescription,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.IndexLeaf:
                CollectIndexLeafOverflowPages(
                    SqliteIndexLeafPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader),
                    activePages,
                    pageCount,
                    overflowReader,
                    $"{treeDescription} {owner}");
                return 0;
            case SqliteBtreePageType.IndexInterior:
                {
                    var interior = SqliteIndexInteriorPageView.Parse(
                        pageImage,
                        _usableSpace,
                        _textEncoding,
                        overflowReader: overflowReader);
                    foreach (var cell in interior.Cells)
                    {
                        CollectIndexOverflowPages(
                            cell.Cell.Key,
                            activePages,
                            pageCount,
                            overflowReader,
                            $"{treeDescription} interior separator");
                    }

                    int? childHeight = null;
                    foreach (var childPage in interior.Cells
                                 .Select(cell => cell.Cell.LeftChildPage)
                                 .Append(interior.Header.RightMostChildPage))
                    {
                        AddOwnedPage(
                            activePages,
                            childPage,
                            pageCount,
                            $"{treeDescription} interior child {pageNumber}");
                        var height = CollectIndexTreeNodePages(
                            treeDescription,
                            childPage,
                            _pager.ReadCommittedPage(childPage),
                            activePages,
                            pageCount,
                            overflowReader,
                            $"interior child {pageNumber}");
                        if (childHeight is { } expectedHeight && height != expectedHeight)
                        {
                            throw new InvalidDataException(
                                $"Stored {treeDescription} interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
                        }

                        childHeight = height;
                    }

                    return checked((childHeight ?? throw new InvalidDataException(
                        $"Stored {treeDescription} has an empty interior page {pageNumber}.")) + 1);
                }
            default:
                throw new InvalidDataException(
                    $"Stored {treeDescription} {owner} page {pageNumber} has unsupported type {header.PageType}.");
        }
    }

    private static void CollectTableLeafOverflowPages(
        SqliteTableLeafPageView leaf,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        foreach (var cell in leaf.Cells)
        {
            CollectTableOverflowPages(
                cell.Cell,
                activePages,
                pageCount,
                overflowReader,
                owner);
        }
    }

    private static void CollectIndexLeafOverflowPages(
        SqliteIndexLeafPageView leaf,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        foreach (var cell in leaf.Cells)
        {
            CollectIndexOverflowPages(
                cell.Cell,
                activePages,
                pageCount,
                overflowReader,
                owner);
        }
    }

    private static void CollectTableOverflowPages(
        SqliteTableLeafCell cell,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        var overflowLength = GetOverflowLength(cell.PayloadLength, cell.LocalPayload.Length, owner);
        if (overflowLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException($"SQLite {owner} cell has an unnecessary overflow page.");
            return;
        }

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException($"SQLite {owner} cell is missing its overflow page.");

        foreach (var overflowPage in overflowReader.Traverse(firstOverflowPage, overflowLength))
            AddOwnedPage(activePages, overflowPage, pageCount, $"{owner} overflow");
    }

    private static void CollectIndexOverflowPages(
        SqliteIndexLeafCell cell,
        ISet<uint> activePages,
        uint pageCount,
        SqliteOverflowChainReader overflowReader,
        string owner)
    {
        var overflowLength = GetOverflowLength(cell.PayloadLength, cell.LocalPayload.Length, owner);
        if (overflowLength == 0)
        {
            if (cell.FirstOverflowPage is not null)
                throw new InvalidDataException($"SQLite {owner} cell has an unnecessary overflow page.");
            return;
        }

        if (cell.FirstOverflowPage is not { } firstOverflowPage)
            throw new InvalidDataException($"SQLite {owner} cell is missing its overflow page.");

        foreach (var overflowPage in overflowReader.Traverse(firstOverflowPage, overflowLength))
            AddOwnedPage(activePages, overflowPage, pageCount, $"{owner} overflow");
    }

    private static ulong GetOverflowLength(ulong payloadLength, int localPayloadLength, string owner)
    {
        if (localPayloadLength < 0 || (ulong)localPayloadLength > payloadLength)
            throw new InvalidDataException($"SQLite {owner} cell local payload exceeds its logical payload.");

        return payloadLength - (ulong)localPayloadLength;
    }

    private static void AddOwnedPage(ISet<uint> activePages, uint pageNumber, uint pageCount, string owner)
    {
        if (pageNumber == 0 || pageNumber > pageCount)
            throw new InvalidDataException($"SQLite {owner} references invalid page {pageNumber}.");
        if (!activePages.Add(pageNumber))
            throw new InvalidDataException($"SQLite {owner} reuses page {pageNumber}.");
    }

    private List<SchemaEntry> ReadSchemaEntries()
    {
        var entries = new List<SchemaEntry>();
        var occupiedPages = new HashSet<uint> { SchemaRootPage };
        long? previousRowId = null;
        _ = ReadSchemaTreeNodeEntries(
            SchemaRootPage,
            _pager.ReadCommittedPage(SchemaRootPage),
            isRoot: true,
            occupiedPages,
            ref previousRowId,
            entries);
        return entries;
    }

    private SchemaTreeReadResult ReadSchemaTreeNodeEntries(
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        bool isRoot,
        ISet<uint> occupiedPages,
        ref long? previousRowId,
        ICollection<SchemaEntry> entries)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage, isFirstPage: isRoot);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    var leaf = SqliteTableLeafPageView.Parse(
                        pageImage,
                        _usableSpace,
                        isFirstPage: isRoot);
                    if (leaf.Cells.Count == 0)
                    {
                        if (isRoot)
                            return new SchemaTreeReadResult(null, 0);
                        throw new EmbeddedSqlException("Managed file database sqlite_schema has an empty leaf child page.");
                    }

                    foreach (var cell in leaf.Cells)
                    {
                        if (previousRowId is { } previous && cell.Cell.RowId <= previous)
                        {
                            throw new EmbeddedSqlException(
                                "Managed file database sqlite_schema rows are not globally ordered by rowid.");
                        }

                        var values = DecodeCellRecord(cell.Cell);
                        if (values.Length != SchemaColumnCount)
                            throw new EmbeddedSqlException("Managed file database has a malformed sqlite_schema row.");

                        entries.Add(new SchemaEntry(
                            RequireText(values[0], "type"),
                            RequireText(values[1], "name"),
                            RequireText(values[2], "tbl_name"),
                            checked((uint)RequireInteger(values[3], "rootpage")),
                            RequireText(values[4], "sql")));
                        previousRowId = cell.Cell.RowId;
                    }

                    return new SchemaTreeReadResult(previousRowId, 0);
                }
            case SqliteBtreePageType.TableInterior:
                {
                    var interior = SqliteTableInteriorPageView.Parse(
                        pageImage,
                        _usableSpace,
                        isFirstPage: isRoot);
                    if (interior.Cells.Count == 0)
                        throw new EmbeddedSqlException("Managed file database sqlite_schema has an empty interior page.");

                    int? childHeight = null;
                    long? maximumRowId = null;
                    for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
                    {
                        var childPage = childIndex == interior.Cells.Count
                            ? interior.Header.RightMostChildPage
                            : interior.Cells[childIndex].Cell.LeftChildPage;
                        if (childPage < 2 || childPage > _pager.CommittedPageCount)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema references invalid child page {childPage}.");
                        }
                        if (!occupiedPages.Add(childPage))
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema reuses child page {childPage}.");
                        }

                        var child = ReadSchemaTreeNodeEntries(
                            childPage,
                            _pager.ReadCommittedPage(childPage),
                            isRoot: false,
                            occupiedPages,
                            ref previousRowId,
                            entries);
                        if (child.MaximumRowId is not { } childMaximumRowId)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema has an empty child page {childPage}.");
                        }
                        if (childHeight is { } expectedHeight && child.Height != expectedHeight)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema interior page {pageNumber} has children with inconsistent heights.");
                        }
                        if (childIndex < interior.Cells.Count
                            && childMaximumRowId != interior.Cells[childIndex].Cell.RowId)
                        {
                            throw new EmbeddedSqlException(
                                $"Managed file database sqlite_schema interior page {pageNumber} has an invalid separator at index {childIndex}.");
                        }

                        childHeight = child.Height;
                        maximumRowId = childMaximumRowId;
                    }

                    return new SchemaTreeReadResult(
                        maximumRowId ?? throw new EmbeddedSqlException(
                            $"Managed file database sqlite_schema has an empty interior page {pageNumber}."),
                        checked((childHeight ?? throw new EmbeddedSqlException(
                            $"Managed file database sqlite_schema has an empty interior page {pageNumber}.")) + 1));
                }
            default:
                throw new EmbeddedSqlException(
                    $"Managed file database sqlite_schema root has unsupported SQLite page type {header.PageType}.");
        }
    }

    private void LoadTableRows(
        string tableName,
        EmbeddedTable table,
        uint rootPage,
        ISet<uint> occupiedBtreePages)
    {
        if (rootPage < 2)
            throw new EmbeddedSqlException($"Managed file database references an invalid rootpage {rootPage}.");

        if (table.WithoutRowid)
        {
            LoadWithoutRowidTableRows(tableName, table, rootPage, occupiedBtreePages);
            return;
        }

        var page = _pager.ReadCommittedPage(rootPage);
        var header = SqliteBtreePageHeader.Parse(page);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    var view = SqliteTableLeafPageView.Parse(page, _usableSpace, isFirstPage: false);
                    LoadTableLeafRows(table, view, previousRowId: null);
                    return;
                }
            case SqliteBtreePageType.TableInterior:
                LoadTableInteriorRows(table, rootPage, page, occupiedBtreePages);
                return;
            default:
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} has unsupported SQLite page type {header.PageType}.");
        }
    }

    private void LoadTableInteriorRows(
        EmbeddedTable table,
        uint rootPage,
        ReadOnlySpan<byte> rootPageImage,
        ISet<uint> occupiedBtreePages)
    {
        long? previousRowId = null;
        _ = LoadTableTreeNodeRows(
            table,
            rootPage,
            rootPage,
            rootPageImage,
            occupiedBtreePages,
            ref previousRowId,
            isRoot: true);
    }

    private TableTreeReadResult LoadTableTreeNodeRows(
        EmbeddedTable table,
        uint rootPage,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        ISet<uint> occupiedBtreePages,
        ref long? previousRowId,
        bool isRoot)
    {
        var header = SqliteBtreePageHeader.Parse(pageImage);
        switch (header.PageType)
        {
            case SqliteBtreePageType.TableLeaf:
                {
                    SqliteTableLeafPageView leaf;
                    try
                    {
                        leaf = SqliteTableLeafPageView.Parse(pageImage, _usableSpace, isFirstPage: false);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table rootpage {rootPage} has non-leaf child page {pageNumber}.",
                            exception);
                    }

                    if (!isRoot && leaf.Cells.Count == 0)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table rootpage {rootPage} has an empty leaf child page {pageNumber}.");
                    }

                    var leafMaximumRowId = LoadTableLeafRows(table, leaf, previousRowId);
                    if (leafMaximumRowId is null)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table rootpage {rootPage} has an empty leaf child page {pageNumber}.");
                    }

                    previousRowId = leafMaximumRowId;
                    return new TableTreeReadResult(leafMaximumRowId.Value, 0);
                }
            case SqliteBtreePageType.TableInterior:
                break;
            default:
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} has unsupported page type {header.PageType} at page {pageNumber}.");
        }

        var interior = SqliteTableInteriorPageView.Parse(pageImage, _usableSpace);
        SqliteBtreePageType? directChildType = null;
        foreach (var childPage in interior.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(interior.Header.RightMostChildPage))
        {
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references invalid child page {childPage}.");
            }

            var currentChildType = SqliteBtreePageHeader.Parse(_pager.ReadCommittedPage(childPage)).PageType;
            if (currentChildType is not (SqliteBtreePageType.TableLeaf or SqliteBtreePageType.TableInterior))
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references unsupported child type {currentChildType}.");
            }
            if (directChildType is { } expectedChildType && currentChildType != expectedChildType)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
            }

            directChildType = currentChildType;
        }

        int? childHeight = null;
        long? maximumRowId = null;
        SqliteBtreePageType? childType = null;
        for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
        {
            var childPage = childIndex == interior.Cells.Count
                ? interior.Header.RightMostChildPage
                : interior.Cells[childIndex].Cell.LeftChildPage;
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references invalid child page {childPage}.");
            }
            if (!occupiedBtreePages.Add(childPage))
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} reuses b-tree page {childPage} as a child.");
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            var currentChildType = SqliteBtreePageHeader.Parse(childPageImage).PageType;
            if (currentChildType is not (SqliteBtreePageType.TableLeaf or SqliteBtreePageType.TableInterior))
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} references unsupported child type {currentChildType}.");
            }
            if (childType is { } expectedChildType && currentChildType != expectedChildType)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
            }

            childType = currentChildType;
            var childResult = LoadTableTreeNodeRows(
                table,
                rootPage,
                childPage,
                childPageImage,
                occupiedBtreePages,
                ref previousRowId,
                isRoot: false);
            if (childHeight is { } expectedHeight && childResult.Height != expectedHeight)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} mixes table-leaf and table-interior non-leaf children.");
            }

            childHeight = childResult.Height;
            if (childIndex < interior.Cells.Count
                && childResult.MaximumRowId != interior.Cells[childIndex].Cell.RowId)
            {
                throw new EmbeddedSqlException(
                    $"Managed file database table rootpage {rootPage} interior page {pageNumber} separator {childIndex} does not match child page {childPage}.");
            }

            maximumRowId = childResult.MaximumRowId;
        }

        return new TableTreeReadResult(
            maximumRowId ?? throw new EmbeddedSqlException(
                $"Managed file database table rootpage {rootPage} has an empty interior page {pageNumber}."),
            checked((childHeight ?? throw new EmbeddedSqlException(
                $"Managed file database table rootpage {rootPage} has an empty interior page {pageNumber}.")) + 1));
    }

    private long? LoadTableLeafRows(
        EmbeddedTable table,
        SqliteTableLeafPageView view,
        long? previousRowId)
    {
        var aliasIndex = table.RowidAliasColumnIndex;
        foreach (var cell in view.Cells)
        {
            if (previousRowId is { } previous && cell.Cell.RowId <= previous)
            {
                throw new EmbeddedSqlException(
                    "Managed file database table leaves are not globally ordered by rowid.");
            }

            var values = DecodeCellRecord(cell.Cell);
            if (values.Length != table.ColumnDefinitions.Length)
                throw new EmbeddedSqlException($"Managed file database row for table has {values.Length} column(s) but the schema declares {table.ColumnDefinitions.Length}.");

            if (aliasIndex >= 0)
                values[aliasIndex] = SqlValue.Integer(cell.Cell.RowId);

            // Preserve the on-disk rowid so both alias and hidden-rowid tables keep their
            // identity across reopen, exactly as SQLite does.
            table.Rows.Add(values);
            table.RowIds.Add(cell.Cell.RowId);
            previousRowId = cell.Cell.RowId;
        }

        return previousRowId;
    }

    private void LoadWithoutRowidTableRows(
        string tableName,
        EmbeddedTable table,
        uint rootPage,
        ISet<uint> occupiedBtreePages)
    {
        var primaryKeySchema = ValidateWithoutRowidTableRepresentable(tableName, table);
        try
        {
            var rootPageImage = _pager.ReadCommittedPage(rootPage);
            var overflowReader = new SqliteOverflowChainReader(_pager, _header);
            var rootHeader = SqliteBtreePageHeader.Parse(rootPageImage);
            var records = rootHeader.PageType switch
            {
                SqliteBtreePageType.IndexLeaf => ReadIndexLeafRecords(rootPageImage, overflowReader),
                SqliteBtreePageType.IndexInterior => ReadIndexInteriorRecords(
                    new SchemaEntry("table", tableName, tableName, rootPage, string.Empty),
                    rootPageImage,
                    overflowReader,
                    occupiedBtreePages),
                _ => throw new InvalidDataException(
                    $"Stored WITHOUT ROWID table '{tableName}' root page has unsupported type {rootHeader.PageType}."),
            };
            var comparer = new SqliteIndexRecordComparer(_textEncoding);
            SqlValue[]? previousKey = null;
            var syntheticRowId = 0L;

            foreach (var record in records)
            {
                var storedValues = SqliteRecordCodec.Decode(record, _textEncoding);
                var row = RestoreWithoutRowidRecord(tableName, table, primaryKeySchema, storedValues);
                var key = primaryKeySchema.ProjectKey(row);
                if (key.Any(value => value.Kind == SqlValueKind.Null))
                {
                    throw new InvalidDataException(
                        $"Stored WITHOUT ROWID table '{tableName}' contains a NULL primary-key value.");
                }
                if (previousKey is not null && comparer.Compare(previousKey, key) >= 0)
                {
                    throw new InvalidDataException(
                        $"Stored WITHOUT ROWID table '{tableName}' primary keys are not strictly increasing in BINARY order.");
                }

                table.Rows.Add(row);
                table.RowIds.Add(checked(++syntheticRowId));
                previousKey = key;
            }

            table.ValidateRows(table.Rows);
        }
        catch (EmbeddedSqlException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new EmbeddedSqlException(
                $"Stored WITHOUT ROWID table '{tableName}' is not a valid supported SQLite index b-tree.",
                exception);
        }
    }

    private SqlValue[] DecodeCellRecord(SqliteTableLeafCell cell)
    {
        var payload = cell.FirstOverflowPage is null
            ? cell.LocalPayload.ToArray()
            : new SqliteOverflowChainReader(_pager, _header).ReadPayload(cell);
        return SqliteRecordCodec.Decode(payload, _textEncoding);
    }

    /// <summary>
    /// Validates and durably persists the full managed catalog as SQLite pages in
    /// a single atomic WAL transaction. Any unsupported schema or data is rejected
    /// before a byte is written so the on-disk database is never left invalid.
    /// </summary>
    public void Persist(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers)
        => PersistCore(tables, views, triggers, reclaimTrailingPages: false);

    /// <summary>
    /// Rebuilds the current managed catalog into the smallest complete page image
    /// the managed writer can represent, then checkpoints and physically removes
    /// its retired suffix. It is intentionally not wired to SQL <c>VACUUM</c>:
    /// this limited writer has not implemented VACUUM's full SQL and transaction
    /// semantics.
    /// </summary>
    internal void Compact()
    {
        ThrowIfDisposed();
        var catalog = Load();
        PersistCore(catalog.Tables, catalog.Views, catalog.Triggers, reclaimTrailingPages: true);
    }

    private void PersistCore(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        bool reclaimTrailingPages)
    {
        ThrowIfDisposed();
        ThrowIfPostCommitMaintenanceFaulted();

        // Validate first: a reject must leave the existing database untouched.
        foreach (var (name, table) in tables)
            EmbeddedFileStore.ValidateTableRepresentable(name, table);
        ValidateSchemaDefinitions(tables, views, triggers);

        if (!reclaimTrailingPages && TryPersistBoundedTableLeafMutation(tables, views, triggers))
            return;

        var currentHeader = SqliteDatabaseHeader.Parse(_pager.ReadCommittedPage(SchemaRootPage));
        var currentFreelist = SqliteFreelist.Read(
            currentHeader,
            _pager.CommittedPageCount,
            _pager.ReadCommittedPage);
        // Only a fully rebuilt page map can safely repurpose existing freelist
        // pages: all new data, trunks, leaves, and page 1 are one WAL commit.
        var allocator = new RebuildPageAllocator(
            _pager.CommittedPageCount,
            currentFreelist.LeafPageNumbers,
            reclaimTrailingPages);
        var tableNames = tables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var indexes = GetIndexDefinitions(tableNames, tables, views, triggers);
        var rootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var indexRootPages = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tableNames)
            rootPages[name] = allocator.ReservePage();
        foreach (var definition in indexes)
            indexRootPages[definition.Index.Name] = allocator.ReservePage();

        // Build every page image up front so a build failure also rejects cleanly.
        var tablePages = new Dictionary<uint, PreparedTableTree>();
        var indexPages = new Dictionary<uint, PreparedIndexTree>();
        foreach (var name in tableNames)
        {
            var table = tables[name];
            tablePages[rootPages[name]] = table.WithoutRowid
                ? BuildWithoutRowidTableTree(name, table, allocator)
                : BuildTableTree(name, table, allocator);
        }
        foreach (var definition in indexes)
        {
            indexPages[indexRootPages[definition.Index.Name]] = BuildIndexTree(
                definition.TableName,
                definition.Table,
                definition.Index,
                allocator);
        }

        var schemaEntries = BuildSchemaEntries(tables, views, triggers, rootPages, indexRootPages);
        var schemaTree = BuildSchemaTree(schemaEntries, allocator);
        var activePages = CollectRewriteActivePages(
            schemaTree,
            tableNames,
            rootPages,
            tablePages,
            indexes,
            indexRootPages,
            indexPages);
        var target = reclaimTrailingPages
            ? allocator.HighestAllocatedPage
            : Math.Max(_pager.CommittedPageCount, allocator.HighestAllocatedPage);
        var freelist = SqliteFreelist.CreateFromFreePages(
            target,
            EnumerateFreePages(target, activePages),
            _pageSize,
            _usableSpace);

        var signature = ComputeSchemaSignature(schemaEntries);
        var schemaChanged = !string.Equals(signature, _lastSchemaSignature, StringComparison.Ordinal);

        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            DatabaseSizeInPages = target,
            FirstFreelistTrunkPage = freelist.FirstTrunkPage,
            FreelistPageCount = freelist.PageCount,
            SchemaCookie = schemaChanged ? currentHeader.SchemaCookie + 1 : currentHeader.SchemaCookie,
        };
        newHeader.WriteTo(schemaTree.RootPage);
        ValidateRewritePlan(
            target,
            schemaTree,
            tableNames,
            rootPages,
            tablePages,
            indexes,
            indexRootPages,
            indexPages,
            freelist);

        using (var transaction = _pager.BeginTransaction(target))
        {
            foreach (var name in tableNames)
            {
                var tablePage = tablePages[rootPages[name]];
                transaction.WritePage(rootPages[name], tablePage.RootPage);
                foreach (var interiorPage in tablePage.InteriorPages)
                    transaction.WritePage(interiorPage.PageNumber, interiorPage.Page);
                foreach (var leafPage in tablePage.LeafPages)
                    transaction.WritePage(leafPage.PageNumber, leafPage.Page);
                foreach (var overflowPage in tablePage.OverflowPages)
                    transaction.WritePage(overflowPage.PageNumber, overflowPage.Page);
            }
            foreach (var definition in indexes)
            {
                var indexPage = indexPages[indexRootPages[definition.Index.Name]];
                transaction.WritePage(indexRootPages[definition.Index.Name], indexPage.RootPage);
                foreach (var interiorPage in indexPage.InteriorPages)
                    transaction.WritePage(interiorPage.PageNumber, interiorPage.Page);
                foreach (var leafPage in indexPage.LeafPages)
                    transaction.WritePage(leafPage.PageNumber, leafPage.Page);
                foreach (var overflowPage in indexPage.OverflowPages)
                    transaction.WritePage(overflowPage.PageNumber, overflowPage.Page);
            }

            foreach (var interiorPage in schemaTree.InteriorPages)
                transaction.WritePage(interiorPage.PageNumber, interiorPage.Page);
            foreach (var leafPage in schemaTree.LeafPages)
                transaction.WritePage(leafPage.PageNumber, leafPage.Page);
            foreach (var freelistPage in freelist.PageImages)
                transaction.WritePage(freelistPage.PageNumber, freelistPage.Page.Span);

            // Page one carries the authoritative size and catalog routing, so it
            // must be the final frame that makes every replacement page visible.
            transaction.WritePage(SchemaRootPage, schemaTree.RootPage);
            transaction.Commit();
        }

        // A full catalog rewrite rewrites every managed page. Once its exclusive
        // checkpoint has durably installed that view, retain neither its WAL frames
        // nor overlay so later rewrites do not rescan an unbounded history.
        CheckpointCommittedMutation(reclaimTrailingPages);

        _header = newHeader;
        _tableRootPages = rootPages;
        _indexRootPages = indexRootPages;
        _lastSchemaSignature = signature;
    }

    /// <summary>
    /// Replaces one existing ordinary-table leaf and every compatible
    /// secondary-index root leaf, promotes compatible secondary-index roots
    /// into interior roots with append-only leaves, or splits the right-most
    /// child leaf of one compatible secondary-index interior root. It can delete
    /// a record from a compatible direct child or retire a singleton child after
    /// moving that child's adjacent separator into its surviving sibling.
    /// </summary>
    /// <remarks>
    /// This intentionally accepts only an unchanged schema, exactly one changed rowid
    /// table, and fully local replacements that fit in its current
    /// table and ASC BINARY index root leaves. Existing freelist pages remain
    /// untouched; new split children are always appended after the committed page
    /// count. In addition to compatible root
    /// promotions, it can replace one non-overflow record or delete one
    /// non-empty child-leaf record beneath a one-level unindexed table root.
    /// A deletion can also collapse an exactly-two-child root back into its
    /// catalog-root leaf when every surviving record fits there, returning both
    /// retired children to a new exact freelist. It can remove an empty child
    /// from a compatible one-level root with at least three children, returning
    /// that retired child to a new exact freelist. A deletion that changes a
    /// non-rightmost child's maximum rowid replaces its parent separator in the
    /// same transaction. In an unindexed tree, a validated ancestor path updates
    /// the nearest separator that owns the changed subtree maximum at arbitrary
    /// depth. It can also append one
    /// maximum-rowid record to the right-most table leaf and split that leaf
    /// when its parent has room for the new separator. When that one-level
    /// unindexed table root is full, it can promote the root while splitting
    /// its right-most leaf, provided every child is a non-overflow leaf and
    /// the mutation is a strict right-most append. For exactly one
    /// ASC BINARY secondary index, it can likewise split the right-most
    /// non-overflow index leaf under a one-level root when the target adds one
    /// maximum complete key, or split a full non-rightmost child when one new
    /// complete key fits strictly between its adjacent parent separators and the
    /// parent can accept the promoted separator. It can atomically insert or delete
    /// one record in direct middle children, or append one strict maximum record to
    /// direct right-most children, of multiple compatible one-level index roots when
    /// no parent separator changes, and it can insert one record into
    /// the left-most direct children of multiple compatible one-level index roots.
    /// It can delete a singleton direct
    /// child when its parent retains at least two separators, transferring the
    /// removed child's adjacent separator into its surviving sibling before
    /// freelisting the retired page.
    /// All routing changes retain their catalog rootpage and are
    /// published with the replacement table page in one WAL transaction whose
    /// final frame is page one. Any overflow ownership change, unproven
    /// topology, rebalance, unsupported index coordination,
    /// root-type change, multi-table mutation, or nonempty freelist returns false
    /// before a write so the complete catalog rewrite remains the safe fallback.
    /// </remarks>
    private bool TryPersistBoundedTableLeafMutation(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers)
    {
        var schemaPage = _pager.ReadCommittedPage(SchemaRootPage);
        var currentHeader = SqliteDatabaseHeader.Parse(schemaPage);
        if (currentHeader.FreelistPageCount != 0 || currentHeader.FirstFreelistTrunkPage != 0)
            return false;

        if (!HasCurrentSchemaShape(tables, views, triggers))
            return false;

        var persisted = Load();
        if (!HasCurrentSchemaShape(tables, views, triggers)
            || !TryGetSingleChangedTable(tables, persisted.Tables, out var tableName, out var table))
        {
            return false;
        }

        if (table.WithoutRowid
            || table.Rows.Count != table.RowIds.Count
            || !_tableRootPages.TryGetValue(tableName, out var rootPage)
            || rootPage < 2
            || rootPage > _pager.CommittedPageCount)
        {
            return false;
        }

        var tableNames = tables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var indexes = GetIndexDefinitions(tableNames, tables, views, triggers)
            .Where(index => string.Equals(index.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (indexes.Length != table.Indexes.Count
            || indexes.Any(index => !IsBoundedIndexLeafMutationCompatible(index)))
            return false;

        var mutationRootPages = new HashSet<uint> { SchemaRootPage, rootPage };
        var affectedIndexNames = new HashSet<string>(
            indexes.Select(index => index.Index.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var index in indexes)
        {
            if (!_indexRootPages.TryGetValue(index.Index.Name, out var indexRootPage)
                || indexRootPage < 2
                || indexRootPage > _pager.CommittedPageCount
                || !mutationRootPages.Add(indexRootPage))
            {
                return false;
            }
        }

        if (_tableRootPages.Any(entry =>
                !string.Equals(entry.Key, tableName, StringComparison.OrdinalIgnoreCase)
                && mutationRootPages.Contains(entry.Value))
            || _indexRootPages.Any(entry =>
                !affectedIndexNames.Contains(entry.Key)
                && mutationRootPages.Contains(entry.Value)))
        {
            return false;
        }

        if (currentHeader.PageSize != _pageSize
            || currentHeader.VersionValidFor != currentHeader.ChangeCounter
            || currentHeader.DatabaseSizeInPages != _pager.CommittedPageCount
            || currentHeader.LargestRootBtreePage != 0
            || currentHeader.IncrementalVacuumEnabled != 0)
        {
            return false;
        }

        if (SqliteBtreePageHeader.Parse(schemaPage, isFirstPage: true).PageType
            is not (SqliteBtreePageType.TableLeaf or SqliteBtreePageType.TableInterior))
            return false;

        var existingPage = _pager.ReadCommittedPage(rootPage);
        var existingPageType = SqliteBtreePageHeader.Parse(existingPage).PageType;
        if (existingPageType == SqliteBtreePageType.TableInterior)
        {
            return indexes.Length == 0
                   && (TryPersistBoundedTableInteriorNestedLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistBoundedTableInteriorThirdLevelLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistValidatedTableInteriorArbitraryDepthLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistBoundedTableInteriorRootSingleLeafMutation(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                       || TryPersistBoundedTableInteriorRootRightLeafAppend(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader)
                        || TryPersistBoundedTableInteriorRootRightLeafAppendWithRootPromotion(
                          tableName,
                          table,
                          persisted.Tables[tableName],
                          rootPage,
                          schemaPage,
                          existingPage,
                          currentHeader));
        }
        if (existingPageType != SqliteBtreePageType.TableLeaf)
        {
            throw new InvalidDataException(
                $"Managed file database table '{tableName}' rootpage {rootPage} has unsupported page type {existingPageType}.");
        }

        var existingLeaf = SqliteTableLeafPageView.Parse(existingPage, _usableSpace, isFirstPage: false);
        if (existingLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            return false;

        if (!TryBuildBoundedTableLeafImage(tableName, table, existingPage, out var replacementPage))
        {
            return indexes.Length == 0
                   && TryPersistBoundedTableRootLeafPromotion(
                       tableName,
                       table,
                       rootPage,
                       schemaPage,
                       existingPage,
                       currentHeader);
        }

        var indexRootImages = indexes
            .Select(index =>
            {
                var indexRootPage = _indexRootPages[index.Index.Name];
                return (Definition: index, RootPage: indexRootPage, SourcePage: _pager.ReadCommittedPage(indexRootPage));
            })
            .ToArray();
        if (indexRootImages.Length > 1
            && indexRootImages.All(index =>
                SqliteBtreePageHeader.Parse(index.SourcePage).PageType == SqliteBtreePageType.IndexInterior))
        {
            return TryPersistBoundedSecondaryIndexInteriorRootLeafInsertions(
                       table,
                       persisted.Tables[tableName],
                       rootPage,
                       replacementPage,
                       indexRootImages,
                       schemaPage,
                       existingPage,
                       currentHeader)
                   || TryPersistBoundedSecondaryIndexInteriorRootLeafDeletions(
                       table,
                       persisted.Tables[tableName],
                       rootPage,
                       replacementPage,
                       indexRootImages,
                       schemaPage,
                       existingPage,
                       currentHeader);
        }

        var indexReplacementPages = new List<(uint PageNumber, byte[] SourcePage, byte[] ReplacementPage)>(
            indexes.Length);
        var indexRootPromotions = new List<BoundedIndexRootLeafPromotion>();
        foreach (var index in indexes)
        {
            var indexRootPage = _indexRootPages[index.Index.Name];
            var existingIndexPage = indexRootImages
                .Single(image => string.Equals(
                    image.Definition.Index.Name,
                    index.Index.Name,
                    StringComparison.OrdinalIgnoreCase))
                .SourcePage;
            var existingIndexPageType = SqliteBtreePageHeader.Parse(existingIndexPage).PageType;
            if (existingIndexPageType == SqliteBtreePageType.IndexInterior)
            {
                return indexes.Length == 1
                       && (TryPersistBoundedSecondaryIndexInteriorRootLeafDeletion(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootLeafInsertion(
                               table,
                               persisted.Tables[tableName],
                               index,
                               rootPage,
                               replacementPage,
                               indexRootPage,
                               existingIndexPage,
                               schemaPage,
                               existingPage,
                               currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootLeftmostLeafSplit(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootMiddleLeafSplit(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader)
                           || TryPersistBoundedSecondaryIndexInteriorRootRightLeafSplit(
                              table,
                              persisted.Tables[tableName],
                              index,
                              rootPage,
                              replacementPage,
                              indexRootPage,
                              existingIndexPage,
                              schemaPage,
                              existingPage,
                              currentHeader));
            }
            if (existingIndexPageType != SqliteBtreePageType.IndexLeaf)
            {
                throw new InvalidDataException(
                    $"Managed file database index '{index.Index.Name}' rootpage {indexRootPage} has unsupported page type {existingIndexPageType}.");
            }

            var existingIndexLeaf = SqliteIndexLeafPageView.Parse(
                existingIndexPage,
                _usableSpace,
                _textEncoding);
            if (existingIndexLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
                return false;

            if (!TryBuildBoundedIndexLeafImage(index, existingIndexPage, out var replacementIndexPage))
            {
                var comparer = new SqliteIndexRecordComparer(_textEncoding);
                var records = BuildIndexRecords(
                    index.TableName,
                    index.Table,
                    index.Index,
                    comparer);
                ValidateBoundedUniqueIndexRecords(index, records, comparer);
                if (!TryBuildBoundedIndexRootLeafSplitImages(
                        records,
                        out var leftPage,
                        out var rightPage,
                        out var separatorRecord))
                {
                    return false;
                }

                indexRootPromotions.Add(new BoundedIndexRootLeafPromotion(
                    indexRootPage,
                    existingIndexPage,
                    leftPage,
                    rightPage,
                    separatorRecord));
                continue;
            }

            indexReplacementPages.Add((indexRootPage, existingIndexPage, replacementIndexPage));
        }

        if (indexRootPromotions.Count != 0)
        {
            return TryPersistBoundedSecondaryIndexRootLeafPromotions(
                rootPage,
                replacementPage,
                indexReplacementPages,
                indexRootPromotions,
                schemaPage,
                existingPage,
                currentHeader);
        }

        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(schemaPage);

        using (var transaction = _pager.BeginTransaction(_pager.CommittedPageCount))
        {
            transaction.WritePage(rootPage, replacementPage);
            foreach (var (pageNumber, _, replacementIndexPage) in indexReplacementPages)
                transaction.WritePage(pageNumber, replacementIndexPage);
            transaction.WritePage(SchemaRootPage, schemaPage);
            transaction.Commit();
        }

        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootRightLeafAppend(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue
            || !TryGetBoundedStrictAppendCell(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var appendedCell))
        {
            return false;
        }

        var parent = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length == 0)
            return false;

        foreach (var childPage in childPages)
        {
            if (childPage < 2
                || childPage == rootPage
                || childPage > sourcePageCount)
            {
                return false;
            }

            var childImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childImage).PageType != SqliteBtreePageType.TableLeaf
                || SqliteTableLeafPageView.Parse(childImage, _usableSpace).Cells
                    .Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }
        }

        var targetLeafPage = parent.Header.RightMostChildPage;
        var sourceLeafPage = _pager.ReadCommittedPage(targetLeafPage);
        var sourceLeaf = SqliteTableLeafPageView.Parse(sourceLeafPage, _usableSpace);
        if (sourceLeaf.Cells.Count == 0
            || sourceLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
            || sourceLeaf.Cells.Count > persistedCells.Count)
        {
            return false;
        }

        var sourceLeafStart = persistedCells.Count - sourceLeaf.Cells.Count;
        for (var index = 0; index < sourceLeaf.Cells.Count; index++)
        {
            if (sourceLeaf.Cells[index].Cell.RowId != persistedCells[sourceLeafStart + index].RowId)
                return false;
        }

        var targetCells = new List<SqliteTableLeafCell>(sourceLeaf.Cells.Count + 1);
        targetCells.AddRange(sourceLeaf.Cells.Select(cell => cell.Cell));
        targetCells.Add(appendedCell);
        if (TryBuildBoundedTableLeafPage(targetCells, 0, targetCells.Count, out var replacementLeafPage))
        {
            return CommitBoundedTableInteriorRootLeafMutation(
                targetLeafPage,
                sourceLeafPage,
                replacementLeafPage,
                schemaPage,
                currentHeader);
        }

        if (!TryBuildBoundedTableLeafSplitImages(
                targetCells,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRowId))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementRootPage;
        try
        {
            var parentBuilder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                appendedPage);
            foreach (var cell in parent.Cells)
                parentBuilder.Append(cell.Cell);
            parentBuilder.Append(SqliteTableInteriorCell.Create(targetLeafPage, separatorRowId));
            replacementRootPage = existingRootPage.ToArray();
            parentBuilder.WriteTo(replacementRootPage);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(rootPage, existingRootPage),
                new SqlitePageImage(targetLeafPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(targetLeafPage, leftLeafPage),
                new SqlitePageImage(rootPage, replacementRootPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootRightLeafAppendWithRootPromotion(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages > uint.MaxValue - 3
            || !TryGetBoundedStrictAppendCell(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var appendedCell))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var parent = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length < 2)
            return false;

        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        SqliteTableLeafPageView? sourceRightLeaf = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage == rootPage
                || childPage > sourcePageCount)
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.TableLeaf)
                return false;

            var child = SqliteTableLeafPageView.Parse(childPageImage, _usableSpace);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                || (previousMaximumRowId is { } maximumRowId
                    && child.Cells[0].Cell.RowId <= maximumRowId))
            {
                return false;
            }

            var childMaximumRowId = child.Cells[^1].Cell.RowId;
            if (childIndex < parent.Cells.Count
                && parent.Cells[childIndex].Cell.RowId != childMaximumRowId)
            {
                return false;
            }

            foreach (var cell in child.Cells)
            {
                if (persistedCellIndex >= persistedCells.Count
                    || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                    || !cell.Cell.LocalPayload.Span.SequenceEqual(persistedCells[persistedCellIndex].Record))
                {
                    return false;
                }

                persistedCellIndex++;
            }

            previousMaximumRowId = childMaximumRowId;
            if (childIndex == childPages.Length - 1)
                sourceRightLeaf = child;
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceRightLeaf is null)
        {
            return false;
        }

        var targetCells = new List<SqliteTableLeafCell>(sourceRightLeaf.Cells.Count + 1);
        targetCells.AddRange(sourceRightLeaf.Cells.Select(cell => cell.Cell));
        targetCells.Add(appendedCell);
        if (TryBuildBoundedTableLeafPage(targetCells, 0, targetCells.Count, out _))
            return false;

        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = sourcePageCount + 3,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitWriter(
                _pager,
                new SqliteAppendOnlyPageAllocator(sourcePageCount))
            .PrepareTableInteriorRootRightmostLeafSplit(
                rootPage,
                appendedCell,
                targetSchemaPage);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootRightLeafSplit(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue)
            return false;

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var existingRecords = new List<byte[]>();
        List<byte[]>? sourceRightLeafRecords = null;
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var childRecords = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                existingRecords.Add(record);
                childRecords.Add(record);
                previousRecord = record;
            }

            if (childIndex < parent.Cells.Count)
            {
                var separator = parent.GetRecord(childIndex);
                if (comparer.Compare(previousRecord!, separator) >= 0)
                    return false;

                existingRecords.Add(separator);
                previousRecord = separator;
            }
            else
            {
                sourceRightLeafRecords = childRecords;
            }
        }

        if (sourceRightLeafRecords is null
            || existingRecords.Count == 0
            || previousRecord is null)
        {
            return false;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex])
                || !targetRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
            {
                return false;
            }
        }

        var appendedRecord = targetRecords[^1];
        if (comparer.Compare(previousRecord, appendedRecord) >= 0
            || SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)appendedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var targetRightLeafRecords = new List<byte[]>(sourceRightLeafRecords.Count + 1);
        targetRightLeafRecords.AddRange(sourceRightLeafRecords);
        targetRightLeafRecords.Add(appendedRecord);
        if (TryBuildBoundedIndexLeafPage(
                targetRightLeafRecords,
                0,
                targetRightLeafRecords.Count,
                out _)
            || !TryBuildBoundedIndexRootLeafSplitImages(
                targetRightLeafRecords,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRecord))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementIndexRootPage;
        try
        {
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                appendedPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
                parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            parentBuilder.Append(
                SqliteIndexInteriorCell.Create(
                    parent.Header.RightMostChildPage,
                    separatorRecord,
                    _usableSpace),
                separatorRecord);
            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(parent.Header.RightMostChildPage, _pager.ReadCommittedPage(parent.Header.RightMostChildPage)),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(parent.Header.RightMostChildPage, leftLeafPage),
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafInsertion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (tableRootPage < 2
            || tableRootPage > sourcePageCount
            || indexRootPage < 2
            || indexRootPage > sourcePageCount
            || tableRootPage == indexRootPage)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1 || childPages.Length < 2)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex < 0
            || route.ChildIndex >= parent.Cells.Count
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || (route.ChildIndex > 0
                && comparer.Compare(parent.GetRecord(route.ChildIndex - 1), addedRecord) >= 0)
            || (route.ChildIndex < parent.Cells.Count
                && comparer.Compare(addedRecord, parent.GetRecord(route.ChildIndex)) >= 0))
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        var sourceLeafPage = _pager.ReadCommittedPage(route.ChildPage);
        if (!TryBuildBoundedIndexLeafReplacementPage(
                replacementRecords,
                sourceLeafPage,
                out var replacementLeafPage))
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(route.ChildPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(route.ChildPage, replacementLeafPage),
                // Page one publishes the revised table and index leaf last.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeftmostLeafSplit(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue
            || currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (tableRootPage < 2
            || tableRootPage > sourcePageCount
            || indexRootPage < 2
            || indexRootPage > sourcePageCount
            || tableRootPage == indexRootPage)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex != 0
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || comparer.Compare(addedRecord, parent.GetRecord(0)) >= 0)
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        if (TryBuildBoundedIndexLeafPage(
                replacementRecords,
                0,
                replacementRecords.Count,
                out _)
            || !TryBuildBoundedIndexRootLeafSplitImages(
                replacementRecords,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRecord))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementIndexRootPage;
        try
        {
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                parent.Header.RightMostChildPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex == 0)
                {
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            route.ChildPage,
                            separatorRecord,
                            _usableSpace),
                        separatorRecord);
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            appendedPage,
                            parent.GetRecord(cellIndex),
                            _usableSpace),
                        parent.GetRecord(cellIndex));
                    continue;
                }

                parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            }

            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(route.ChildPage, sourceChildPages[route.ChildIndex]),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(route.ChildPage, leftLeafPage),
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                // The catalog root makes the new left-most child reachable only
                // after every dependent image has been written to the WAL.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootMiddleLeafSplit(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages == uint.MaxValue
            || currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (tableRootPage < 2
            || tableRootPage > sourcePageCount
            || indexRootPage < 2
            || indexRootPage > sourcePageCount
            || tableRootPage == indexRootPage)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count < 2
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex <= 0
            || route.ChildIndex >= parent.Cells.Count
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || comparer.Compare(parent.GetRecord(route.ChildIndex - 1), addedRecord) >= 0
            || comparer.Compare(addedRecord, parent.GetRecord(route.ChildIndex)) >= 0)
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        if (TryBuildBoundedIndexLeafPage(
                replacementRecords,
                0,
                replacementRecords.Count,
                out _)
            || !TryBuildBoundedIndexRootLeafSplitImages(
                replacementRecords,
                out var leftLeafPage,
                out var rightLeafPage,
                out var separatorRecord))
        {
            return false;
        }

        var appendedPage = sourcePageCount + 1;
        byte[] replacementIndexRootPage;
        try
        {
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                parent.Header.RightMostChildPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex == route.ChildIndex)
                {
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            route.ChildPage,
                            separatorRecord,
                            _usableSpace),
                        separatorRecord);
                    parentBuilder.Append(
                        SqliteIndexInteriorCell.Create(
                            appendedPage,
                            parent.GetRecord(cellIndex),
                            _usableSpace),
                        parent.GetRecord(cellIndex));
                    continue;
                }

                parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            }

            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = appendedPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            appendedPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(route.ChildPage, sourceChildPages[route.ChildIndex]),
            ],
            [
                new SqlitePageImage(appendedPage, rightLeafPage),
                new SqlitePageImage(route.ChildPage, leftLeafPage),
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                // The catalog root makes the new child reachable only after every
                // dependent image has been written to the WAL.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafInsertions(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        IReadOnlyList<(IndexDefinition Definition, uint RootPage, byte[] SourcePage)> indexes,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0
            || indexes.Count < 2)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage };
        var insertions = new List<BoundedSecondaryIndexInteriorLeafInsertion>(indexes.Count);
        foreach (var (definition, indexRootPage, sourceIndexRootPage) in indexes)
        {
            if (!TryPrepareBoundedSecondaryIndexInteriorRootLeafInsertion(
                    table,
                    persistedTable,
                    definition,
                    indexRootPage,
                    sourceIndexRootPage,
                    currentHeader,
                    out var insertion))
            {
                return false;
            }

            foreach (var sourcePage in insertion.SourceTreePages)
            {
                if (!ownedPages.Add(sourcePage.PageNumber))
                    return false;
            }

            insertions.Add(insertion);
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(checked(2 + ownedPages.Count))
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(tableRootPage, sourceTablePage),
        };
        foreach (var insertion in insertions)
            sourcePages.AddRange(insertion.SourceTreePages);

        var writeImages = new List<SqlitePageImage>(checked(2 + insertions.Count))
        {
            new(tableRootPage, replacementTablePage),
        };
        writeImages.AddRange(insertions
            .OrderBy(insertion => insertion.LeafPageNumber)
            .Select(insertion => new SqlitePageImage(
                insertion.LeafPageNumber,
                insertion.ReplacementLeafPage)));
        // Page one publishes all table and index leaf replacements only after
        // every routed page image is present in the WAL.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPrepareBoundedSecondaryIndexInteriorRootLeafInsertion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        SqliteDatabaseHeader currentHeader,
        out BoundedSecondaryIndexInteriorLeafInsertion insertion)
    {
        insertion = null!;
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (indexRootPage < 2
            || indexRootPage > sourcePageCount
            || SqliteBtreePageHeader.Parse(sourceIndexRootPage).PageType != SqliteBtreePageType.IndexInterior)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { indexRootPage };
        var sourceTreePages = new List<SqlitePageImage>(childPages.Length + 1)
        {
            new(indexRootPage, sourceIndexRootPage),
        };
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceTreePages.Add(new SqlitePageImage(childPage, childPageImage));
            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count + 1)
        {
            return false;
        }

        var addedRecordIndex = -1;
        var existingRecordIndex = 0;
        for (var targetRecordIndex = 0; targetRecordIndex < targetRecords.Count; targetRecordIndex++)
        {
            if (existingRecordIndex < existingRecords.Count
                && targetRecords[targetRecordIndex].AsSpan()
                    .SequenceEqual(existingRecords[existingRecordIndex]))
            {
                existingRecordIndex++;
                continue;
            }

            if (addedRecordIndex >= 0)
                return false;

            addedRecordIndex = targetRecordIndex;
        }

        if (addedRecordIndex < 0 || existingRecordIndex != existingRecords.Count)
            return false;

        for (var recordIndex = 0; recordIndex < existingRecords.Count; recordIndex++)
        {
            if (!persistedRecords[recordIndex].AsSpan().SequenceEqual(existingRecords[recordIndex]))
                return false;
        }

        var addedRecord = targetRecords[addedRecordIndex];
        if (SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)addedRecord.Length),
                _usableSpace).UsesOverflow)
        {
            return false;
        }

        var route = parent.SearchChild(addedRecord);
        if (route.IsSeparatorKey
            || route.ChildIndex < 0
            || route.ChildIndex >= childPages.Length
            || childPages[route.ChildIndex] != route.ChildPage)
        {
            return false;
        }

        var isRightmostChild = route.ChildIndex == parent.Cells.Count;
        if (isRightmostChild
            && (addedRecordIndex != targetRecords.Count - 1
                || comparer.Compare(previousRecord!, addedRecord) >= 0))
        {
            return false;
        }

        var routedRecords = childRecords[route.ChildIndex];
        var insertionIndex = 0;
        while (insertionIndex < routedRecords.Count
               && comparer.Compare(routedRecords[insertionIndex], addedRecord) < 0)
        {
            insertionIndex++;
        }

        if ((isRightmostChild && insertionIndex != routedRecords.Count)
            || (insertionIndex > 0
             && comparer.Compare(routedRecords[insertionIndex - 1], addedRecord) >= 0)
            || (insertionIndex < routedRecords.Count
                && comparer.Compare(addedRecord, routedRecords[insertionIndex]) >= 0)
            || (route.ChildIndex > 0
                && comparer.Compare(parent.GetRecord(route.ChildIndex - 1), addedRecord) >= 0)
            || (route.ChildIndex < parent.Cells.Count
                && comparer.Compare(addedRecord, parent.GetRecord(route.ChildIndex)) >= 0))
        {
            return false;
        }

        var replacementRecords = new List<byte[]>(routedRecords.Count + 1);
        replacementRecords.AddRange(routedRecords.Take(insertionIndex));
        replacementRecords.Add(addedRecord);
        replacementRecords.AddRange(routedRecords.Skip(insertionIndex));
        if (!TryBuildBoundedIndexLeafReplacementPage(
                replacementRecords,
                sourceChildPages[route.ChildIndex],
                out var replacementLeafPage))
        {
            return false;
        }

        insertion = new BoundedSecondaryIndexInteriorLeafInsertion(
            route.ChildPage,
            replacementLeafPage,
            sourceTreePages);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafDeletions(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        IReadOnlyList<(IndexDefinition Definition, uint RootPage, byte[] SourcePage)> indexes,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0
            || indexes.Count < 2)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage };
        var deletions = new List<BoundedSecondaryIndexInteriorLeafDeletion>(indexes.Count);
        foreach (var (definition, indexRootPage, sourceIndexRootPage) in indexes)
        {
            if (!TryPrepareBoundedSecondaryIndexInteriorRootLeafDeletion(
                    table,
                    persistedTable,
                    definition,
                    indexRootPage,
                    sourceIndexRootPage,
                    currentHeader,
                    out var deletion))
            {
                return false;
            }

            foreach (var sourcePage in deletion.SourceTreePages)
            {
                if (!ownedPages.Add(sourcePage.PageNumber))
                    return false;
            }

            deletions.Add(deletion);
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(checked(2 + ownedPages.Count))
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(tableRootPage, sourceTablePage),
        };
        foreach (var deletion in deletions)
            sourcePages.AddRange(deletion.SourceTreePages);

        var writeImages = new List<SqlitePageImage>(checked(2 + deletions.Count))
        {
            new(tableRootPage, replacementTablePage),
        };
        writeImages.AddRange(deletions
            .OrderBy(deletion => deletion.LeafPageNumber)
            .Select(deletion => new SqlitePageImage(
                deletion.LeafPageNumber,
                deletion.ReplacementLeafPage)));
        // The catalog root publishes all table and index leaf replacements only
        // after every routed page image is present in the WAL.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPrepareBoundedSecondaryIndexInteriorRootLeafDeletion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        SqliteDatabaseHeader currentHeader,
        out BoundedSecondaryIndexInteriorLeafDeletion deletion)
    {
        deletion = null!;
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        if (indexRootPage < 2
            || indexRootPage > sourcePageCount
            || SqliteBtreePageHeader.Parse(sourceIndexRootPage).PageType != SqliteBtreePageType.IndexInterior)
        {
            return false;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1 || childPages.Length < 2)
            return false;

        var ownedPages = new HashSet<uint> { indexRootPage };
        var sourceTreePages = new List<SqlitePageImage>(childPages.Length + 1)
        {
            new(indexRootPage, sourceIndexRootPage),
        };
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceTreePages.Add(new SqlitePageImage(childPage, childPageImage));
            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        if (existingRecords.Count == 0)
            return false;

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count - 1)
        {
            return false;
        }

        var removedRecordIndex = -1;
        var targetRecordIndex = 0;
        for (var existingRecordIndex = 0;
             existingRecordIndex < existingRecords.Count;
             existingRecordIndex++)
        {
            if (targetRecordIndex < targetRecords.Count
                && targetRecords[targetRecordIndex].AsSpan().SequenceEqual(existingRecords[existingRecordIndex]))
            {
                targetRecordIndex++;
                continue;
            }

            if (removedRecordIndex >= 0)
                return false;

            removedRecordIndex = existingRecordIndex;
        }

        if (removedRecordIndex < 0 || targetRecordIndex != targetRecords.Count)
            return false;

        var removedRecord = existingRecords[removedRecordIndex];
        var removedChildIndex = -1;
        var removedChildRecordIndex = -1;
        for (var childIndex = 0; childIndex < childRecords.Count; childIndex++)
        {
            var recordIndex = childRecords[childIndex].FindIndex(
                record => record.AsSpan().SequenceEqual(removedRecord));
            if (recordIndex < 0)
                continue;

            if (removedChildIndex >= 0)
                return false;

            removedChildIndex = childIndex;
            removedChildRecordIndex = recordIndex;
        }

        // A separator is the maximum key of its left child. Rewriting that child
        // would require parent propagation, so this path never attempts it.
        if (removedChildIndex < 0
            || childRecords[removedChildIndex].Count <= 1
            || (removedChildIndex < parent.Cells.Count
                && removedChildRecordIndex == childRecords[removedChildIndex].Count - 1))
        {
            return false;
        }

        var replacementRecords = childRecords[removedChildIndex]
            .Where(record => !record.AsSpan().SequenceEqual(removedRecord))
            .ToArray();
        if (replacementRecords.Length != childRecords[removedChildIndex].Count - 1
            || !TryBuildBoundedIndexLeafReplacementPage(
                replacementRecords,
                sourceChildPages[removedChildIndex],
                out var replacementLeafPage))
        {
            return false;
        }

        deletion = new BoundedSecondaryIndexInteriorLeafDeletion(
            childPages[removedChildIndex],
            replacementLeafPage,
            sourceTreePages);
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexInteriorRootLeafDeletion(
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        IndexDefinition definition,
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        uint indexRootPage,
        ReadOnlySpan<byte> sourceIndexRootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> sourceTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.FreelistPageCount != 0
            || currentHeader.FirstFreelistTrunkPage != 0)
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var parent = SqliteIndexInteriorPageView.Parse(
            sourceIndexRootPage,
            _usableSpace,
            _textEncoding);
        if (parent.Cells.Count == 0
            || parent.Cells.Any(cell => cell.Cell.Key.FirstOverflowPage is not null))
        {
            return false;
        }

        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length != parent.Cells.Count + 1 || childPages.Length < 2)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, tableRootPage, indexRootPage };
        var sourceChildPages = new List<byte[]>(childPages.Length);
        var childRecords = new List<List<byte[]>>(childPages.Length);
        var existingRecords = new List<byte[]>();
        byte[]? previousRecord = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage > sourcePageCount
                || !ownedPages.Add(childPage))
            {
                return false;
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childPageImage).PageType != SqliteBtreePageType.IndexLeaf)
                return false;

            var child = SqliteIndexLeafPageView.Parse(
                childPageImage,
                _usableSpace,
                _textEncoding);
            if (child.Cells.Count == 0
                || child.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null))
            {
                return false;
            }

            var records = new List<byte[]>(child.Cells.Count);
            for (var recordIndex = 0; recordIndex < child.Cells.Count; recordIndex++)
            {
                var record = child.GetRecord(recordIndex);
                if (previousRecord is not null && comparer.Compare(previousRecord, record) >= 0)
                    return false;

                records.Add(record);
                existingRecords.Add(record);
                previousRecord = record;
            }

            sourceChildPages.Add(childPageImage);
            childRecords.Add(records);
            if (childIndex >= parent.Cells.Count)
                continue;

            var separator = parent.GetRecord(childIndex);
            if (comparer.Compare(previousRecord!, separator) >= 0)
                return false;

            existingRecords.Add(separator);
            previousRecord = separator;
        }

        if (existingRecords.Count == 0)
            return false;

        var persistedRecords = BuildIndexRecords(
            definition.TableName,
            persistedTable,
            definition.Index,
            comparer);
        var targetRecords = BuildIndexRecords(
            definition.TableName,
            table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, targetRecords, comparer);
        if (persistedRecords.Count != existingRecords.Count
            || targetRecords.Count != existingRecords.Count - 1)
        {
            return false;
        }

        var removedRecordIndex = -1;
        var targetRecordIndex = 0;
        for (var existingRecordIndex = 0;
             existingRecordIndex < existingRecords.Count;
             existingRecordIndex++)
        {
            if (targetRecordIndex < targetRecords.Count
                && targetRecords[targetRecordIndex].AsSpan().SequenceEqual(existingRecords[existingRecordIndex]))
            {
                targetRecordIndex++;
                continue;
            }

            if (removedRecordIndex >= 0)
                return false;

            removedRecordIndex = existingRecordIndex;
        }

        if (removedRecordIndex < 0 || targetRecordIndex != targetRecords.Count)
            return false;

        var removedRecord = existingRecords[removedRecordIndex];
        var removedChildIndex = -1;
        for (var childIndex = 0; childIndex < childRecords.Count; childIndex++)
        {
            if (childRecords[childIndex].Any(record => record.AsSpan().SequenceEqual(removedRecord)))
            {
                removedChildIndex = childIndex;
                break;
            }
        }

        if (removedChildIndex < 0)
            return false;

        if (childRecords[removedChildIndex].Count > 1)
        {
            var replacementRecords = childRecords[removedChildIndex]
                .Where(record => !record.AsSpan().SequenceEqual(removedRecord))
                .ToArray();
            if (replacementRecords.Length != childRecords[removedChildIndex].Count - 1
                || !TryBuildBoundedIndexLeafReplacementPage(
                    replacementRecords,
                    sourceChildPages[removedChildIndex],
                    out var replacementLeafPage))
            {
                return false;
            }

            var leafSourceSchemaPage = schemaPage.ToArray();
            var leafTargetSchemaPage = schemaPage.ToArray();
            var leafChangeCounter = currentHeader.ChangeCounter + 1;
            var leafHeader = currentHeader with
            {
                ChangeCounter = leafChangeCounter,
                VersionValidFor = leafChangeCounter,
            };
            leafHeader.WriteTo(leafTargetSchemaPage);

            var leafMutation = new SqliteBtreeSplitMutation(
                sourcePageCount,
                sourcePageCount,
                _pageSize,
                [
                    new SqlitePageImage(SchemaRootPage, leafSourceSchemaPage),
                    new SqlitePageImage(tableRootPage, sourceTablePage),
                    new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                    new SqlitePageImage(
                        childPages[removedChildIndex],
                        sourceChildPages[removedChildIndex]),
                ],
                [
                    new SqlitePageImage(tableRootPage, replacementTablePage),
                    new SqlitePageImage(childPages[removedChildIndex], replacementLeafPage),
                    // Page one publishes the revised table and index leaves last.
                    new SqlitePageImage(SchemaRootPage, leafTargetSchemaPage),
                ]);
            leafMutation.CommitTo(_pager);
            CheckpointCommittedMutation(reclaimTrailingPages: false);
            _header = leafHeader;
            return true;
        }

        if (parent.Cells.Count < 2 || childPages.Length < 3)
            return false;

        var removedSeparatorIndex = removedChildIndex == parent.Cells.Count
            ? parent.Cells.Count - 1
            : removedChildIndex;
        var siblingChildIndex = removedChildIndex == parent.Cells.Count
            ? removedChildIndex - 1
            : removedChildIndex + 1;
        var transferredSeparator = parent.GetRecord(removedSeparatorIndex);
        var replacementSiblingRecords = new List<byte[]>(
            childRecords[siblingChildIndex].Count + 1);
        if (siblingChildIndex < removedChildIndex)
        {
            replacementSiblingRecords.AddRange(childRecords[siblingChildIndex]);
            replacementSiblingRecords.Add(transferredSeparator);
        }
        else
        {
            replacementSiblingRecords.Add(transferredSeparator);
            replacementSiblingRecords.AddRange(childRecords[siblingChildIndex]);
        }

        var siblingPage = childPages[siblingChildIndex];
        if (!TryBuildBoundedIndexLeafReplacementPage(
                replacementSiblingRecords,
                sourceChildPages[siblingChildIndex],
                out var replacementSiblingPage))
        {
            return false;
        }

        byte[] replacementIndexRootPage;
        try
        {
            var rightMostChildPage = removedChildIndex == parent.Cells.Count
                ? parent.Cells[^1].Cell.LeftChildPage
                : parent.Header.RightMostChildPage;
            var parentBuilder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                rightMostChildPage,
                comparer);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex != removedSeparatorIndex)
                    parentBuilder.Append(parent.Cells[cellIndex].Cell, parent.GetRecord(cellIndex));
            }

            replacementIndexRootPage = sourceIndexRootPage.ToArray();
            parentBuilder.WriteTo(replacementIndexRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        SqliteFreelist retiredChild;
        try
        {
            retiredChild = SqliteFreelist.CreateFromFreePages(
                sourcePageCount,
                [childPages[removedChildIndex]],
                _pageSize,
                _usableSpace);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (retiredChild.PageCount != 1
            || retiredChild.FirstTrunkPage != childPages[removedChildIndex]
            || retiredChild.PageNumbers.Count != 1
            || retiredChild.PageNumbers[0] != childPages[removedChildIndex]
            || retiredChild.TrunkPageNumbers.Count != 1
            || retiredChild.TrunkPageNumbers[0] != childPages[removedChildIndex]
            || retiredChild.PageImages.Count != 1
            || retiredChild.PageImages[0].PageNumber != childPages[removedChildIndex])
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            FirstFreelistTrunkPage = retiredChild.FirstTrunkPage,
            FreelistPageCount = retiredChild.PageCount,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(tableRootPage, sourceTablePage),
                new SqlitePageImage(indexRootPage, sourceIndexRootPage),
                new SqlitePageImage(childPages[removedChildIndex], sourceChildPages[removedChildIndex]),
                new SqlitePageImage(siblingPage, sourceChildPages[siblingChildIndex]),
            ],
            [
                new SqlitePageImage(tableRootPage, replacementTablePage),
                new SqlitePageImage(siblingPage, replacementSiblingPage),
                new SqlitePageImage(indexRootPage, replacementIndexRootPage),
                retiredChild.PageImages[0],
                // Page one exposes both the revised tree and its retired child.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedTableInteriorNestedLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var root = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        if (root.Cells.Count == 0)
            return false;

        var rootChildPages = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage)
            .ToArray();
        if (rootChildPages.Length != root.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, rootPage };
        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        byte[]? sourceLeafPage = null;
        SqliteTableLeafPageView? sourceLeaf = null;
        uint targetLeafPage = 0;
        byte[]? sourceParentPage = null;
        SqliteTableInteriorPageView? targetParent = null;
        uint targetParentPage = 0;
        var targetParentIndex = -1;
        var targetLeafIndex = -1;

        for (var parentIndex = 0; parentIndex < rootChildPages.Length; parentIndex++)
        {
            var parentPage = rootChildPages[parentIndex];
            if (parentPage < 2
                || parentPage > sourcePageCount
                || !ownedPages.Add(parentPage))
            {
                return false;
            }

            var parentImage = _pager.ReadCommittedPage(parentPage);
            if (SqliteBtreePageHeader.Parse(parentImage).PageType != SqliteBtreePageType.TableInterior)
                return false;

            var parent = SqliteTableInteriorPageView.Parse(parentImage, _usableSpace);
            if (parent.Cells.Count == 0)
                return false;

            var leafPages = parent.Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(parent.Header.RightMostChildPage)
                .ToArray();
            if (leafPages.Length != parent.Cells.Count + 1)
                return false;

            long? parentMaximumRowId = null;
            for (var leafIndex = 0; leafIndex < leafPages.Length; leafIndex++)
            {
                var leafPage = leafPages[leafIndex];
                if (leafPage < 2
                    || leafPage > sourcePageCount
                    || !ownedPages.Add(leafPage))
                {
                    return false;
                }

                var leafImage = _pager.ReadCommittedPage(leafPage);
                if (SqliteBtreePageHeader.Parse(leafImage).PageType != SqliteBtreePageType.TableLeaf)
                    return false;

                var leaf = SqliteTableLeafPageView.Parse(leafImage, _usableSpace);
                if (leaf.Cells.Count == 0
                    || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                    || (previousMaximumRowId is { } maximumRowId
                        && leaf.Cells[0].Cell.RowId <= maximumRowId))
                {
                    return false;
                }

                if (leafIndex < parent.Cells.Count
                    && leaf.Cells[^1].Cell.RowId != parent.Cells[leafIndex].Cell.RowId)
                {
                    return false;
                }

                foreach (var cell in leaf.Cells)
                {
                    if (persistedCellIndex >= persistedCells.Count
                        || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                        || !cell.Cell.LocalPayload.Span.SequenceEqual(
                            persistedCells[persistedCellIndex].Record))
                    {
                        return false;
                    }

                    persistedCellIndex++;
                }

                if (leaf.Search(change.RowId).IsExact)
                {
                    if (sourceLeaf is not null)
                        return false;

                    sourceLeaf = leaf;
                    sourceLeafPage = leafImage;
                    targetLeafPage = leafPage;
                    sourceParentPage = parentImage;
                    targetParent = parent;
                    targetParentPage = parentPage;
                    targetParentIndex = parentIndex;
                    targetLeafIndex = leafIndex;
                }

                parentMaximumRowId = leaf.Cells[^1].Cell.RowId;
                previousMaximumRowId = parentMaximumRowId;
            }

            if (parentMaximumRowId is null
                || (parentIndex < root.Cells.Count
                    && root.Cells[parentIndex].Cell.RowId != parentMaximumRowId.Value))
            {
                return false;
            }
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || sourceParentPage is null
            || targetParent is null
            || targetLeafPage == 0
            || targetParentPage == 0
            || targetParentIndex < 0
            || targetLeafIndex < 0)
        {
            return false;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementParentPage = null;
        byte[]? replacementRootPage = null;
        var boundaryChanged = change.IsDelete
            && sourceLeaf.Cells[^1].Cell.RowId == change.RowId;
        if (boundaryChanged && targetLeafIndex < targetParent.Cells.Count)
        {
            try
            {
                var parentBuilder = new SqliteTableInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    targetParent.Header.RightMostChildPage);
                for (var cellIndex = 0; cellIndex < targetParent.Cells.Count; cellIndex++)
                {
                    var cell = targetParent.Cells[cellIndex].Cell;
                    parentBuilder.Append(cellIndex == targetLeafIndex
                        ? SqliteTableInteriorCell.Create(targetLeafPage, replacementCells[^1].RowId)
                        : cell);
                }

                replacementParentPage = sourceParentPage.ToArray();
                parentBuilder.WriteTo(replacementParentPage);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        else if (boundaryChanged && targetParentIndex < root.Cells.Count)
        {
            try
            {
                var rootBuilder = new SqliteTableInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    root.Header.RightMostChildPage);
                for (var cellIndex = 0; cellIndex < root.Cells.Count; cellIndex++)
                {
                    var cell = root.Cells[cellIndex].Cell;
                    rootBuilder.Append(cellIndex == targetParentIndex
                        ? SqliteTableInteriorCell.Create(targetParentPage, replacementCells[^1].RowId)
                        : cell);
                }

                replacementRootPage = existingRootPage.ToArray();
                rootBuilder.WriteTo(replacementRootPage);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (replacementParentPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(targetParentPage, sourceParentPage));
            writeImages.Add(new SqlitePageImage(targetParentPage, replacementParentPage));
        }
        if (replacementRootPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(rootPage, existingRootPage));
            writeImages.Add(new SqlitePageImage(rootPage, replacementRootPage));
        }

        // Page one remains the WAL commit frame, after every routing image.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedTableInteriorThirdLevelLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var root = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        if (root.Cells.Count == 0)
            return false;

        var rootChildPages = root.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(root.Header.RightMostChildPage)
            .ToArray();
        if (rootChildPages.Length != root.Cells.Count + 1)
            return false;

        var ownedPages = new HashSet<uint> { SchemaRootPage, rootPage };
        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        byte[]? sourceLeafPage = null;
        SqliteTableLeafPageView? sourceLeaf = null;
        uint targetLeafPage = 0;
        byte[]? sourceParentPage = null;
        SqliteTableInteriorPageView? targetParent = null;
        uint targetParentPage = 0;
        var targetLeafIndex = -1;
        byte[]? sourceGrandparentPage = null;
        SqliteTableInteriorPageView? targetGrandparent = null;
        uint targetGrandparentPage = 0;
        var targetParentIndex = -1;
        var targetGrandparentIndex = -1;

        for (var grandparentIndex = 0;
             grandparentIndex < rootChildPages.Length;
             grandparentIndex++)
        {
            var grandparentPage = rootChildPages[grandparentIndex];
            if (grandparentPage < 2
                || grandparentPage > sourcePageCount
                || !ownedPages.Add(grandparentPage))
            {
                return false;
            }

            var grandparentImage = _pager.ReadCommittedPage(grandparentPage);
            if (SqliteBtreePageHeader.Parse(grandparentImage).PageType
                != SqliteBtreePageType.TableInterior)
            {
                return false;
            }

            var grandparent = SqliteTableInteriorPageView.Parse(grandparentImage, _usableSpace);
            if (grandparent.Cells.Count == 0)
                return false;

            var parentPages = grandparent.Cells
                .Select(cell => cell.Cell.LeftChildPage)
                .Append(grandparent.Header.RightMostChildPage)
                .ToArray();
            if (parentPages.Length != grandparent.Cells.Count + 1)
                return false;

            long? grandparentMaximumRowId = null;
            for (var parentIndex = 0; parentIndex < parentPages.Length; parentIndex++)
            {
                var parentPage = parentPages[parentIndex];
                if (parentPage < 2
                    || parentPage > sourcePageCount
                    || !ownedPages.Add(parentPage))
                {
                    return false;
                }

                var parentImage = _pager.ReadCommittedPage(parentPage);
                if (SqliteBtreePageHeader.Parse(parentImage).PageType
                    != SqliteBtreePageType.TableInterior)
                {
                    return false;
                }

                var parent = SqliteTableInteriorPageView.Parse(parentImage, _usableSpace);
                if (parent.Cells.Count == 0)
                    return false;

                var leafPages = parent.Cells
                    .Select(cell => cell.Cell.LeftChildPage)
                    .Append(parent.Header.RightMostChildPage)
                    .ToArray();
                if (leafPages.Length != parent.Cells.Count + 1)
                    return false;

                long? parentMaximumRowId = null;
                for (var leafIndex = 0; leafIndex < leafPages.Length; leafIndex++)
                {
                    var leafPage = leafPages[leafIndex];
                    if (leafPage < 2
                        || leafPage > sourcePageCount
                        || !ownedPages.Add(leafPage))
                    {
                        return false;
                    }

                    var leafImage = _pager.ReadCommittedPage(leafPage);
                    if (SqliteBtreePageHeader.Parse(leafImage).PageType
                        != SqliteBtreePageType.TableLeaf)
                    {
                        return false;
                    }

                    var leaf = SqliteTableLeafPageView.Parse(leafImage, _usableSpace);
                    if (leaf.Cells.Count == 0
                        || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                        || (previousMaximumRowId is { } maximumRowId
                            && leaf.Cells[0].Cell.RowId <= maximumRowId))
                    {
                        return false;
                    }

                    if (leafIndex < parent.Cells.Count
                        && leaf.Cells[^1].Cell.RowId != parent.Cells[leafIndex].Cell.RowId)
                    {
                        return false;
                    }

                    foreach (var cell in leaf.Cells)
                    {
                        if (persistedCellIndex >= persistedCells.Count
                            || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                            || !cell.Cell.LocalPayload.Span.SequenceEqual(
                                persistedCells[persistedCellIndex].Record))
                        {
                            return false;
                        }

                        persistedCellIndex++;
                    }

                    if (leaf.Search(change.RowId).IsExact)
                    {
                        if (sourceLeaf is not null)
                            return false;

                        sourceLeaf = leaf;
                        sourceLeafPage = leafImage;
                        targetLeafPage = leafPage;
                        sourceParentPage = parentImage;
                        targetParent = parent;
                        targetParentPage = parentPage;
                        targetLeafIndex = leafIndex;
                        sourceGrandparentPage = grandparentImage;
                        targetGrandparent = grandparent;
                        targetGrandparentPage = grandparentPage;
                        targetParentIndex = parentIndex;
                        targetGrandparentIndex = grandparentIndex;
                    }

                    parentMaximumRowId = leaf.Cells[^1].Cell.RowId;
                    previousMaximumRowId = parentMaximumRowId;
                }

                if (parentMaximumRowId is null
                    || (parentIndex < grandparent.Cells.Count
                        && grandparent.Cells[parentIndex].Cell.RowId != parentMaximumRowId.Value))
                {
                    return false;
                }

                grandparentMaximumRowId = parentMaximumRowId;
            }

            if (grandparentMaximumRowId is null
                || (grandparentIndex < root.Cells.Count
                    && root.Cells[grandparentIndex].Cell.RowId != grandparentMaximumRowId.Value))
            {
                return false;
            }
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || sourceParentPage is null
            || targetParent is null
            || targetLeafPage == 0
            || targetParentPage == 0
            || targetLeafIndex < 0
            || sourceGrandparentPage is null
            || targetGrandparent is null
            || targetGrandparentPage == 0
            || targetParentIndex < 0
            || targetGrandparentIndex < 0)
        {
            return false;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementParentPage = null;
        byte[]? replacementGrandparentPage = null;
        byte[]? replacementRootPage = null;
        var boundaryChanged = change.IsDelete
            && sourceLeaf.Cells[^1].Cell.RowId == change.RowId;
        if (boundaryChanged)
        {
            if (targetLeafIndex < targetParent.Cells.Count)
            {
                if (!TryReplaceTableInteriorSeparator(
                        sourceParentPage,
                        targetParent,
                        targetLeafIndex,
                        targetLeafPage,
                        replacementCells[^1].RowId,
                        out replacementParentPage))
                {
                    return false;
                }
            }
            else if (targetParentIndex < targetGrandparent.Cells.Count)
            {
                if (!TryReplaceTableInteriorSeparator(
                        sourceGrandparentPage,
                        targetGrandparent,
                        targetParentIndex,
                        targetParentPage,
                        replacementCells[^1].RowId,
                        out replacementGrandparentPage))
                {
                    return false;
                }
            }
            else if (targetGrandparentIndex < root.Cells.Count
                     && !TryReplaceTableInteriorSeparator(
                         existingRootPage,
                         root,
                         targetGrandparentIndex,
                         targetGrandparentPage,
                         replacementCells[^1].RowId,
                         out replacementRootPage))
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (replacementParentPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(targetParentPage, sourceParentPage));
            writeImages.Add(new SqlitePageImage(targetParentPage, replacementParentPage));
        }
        if (replacementGrandparentPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(targetGrandparentPage, sourceGrandparentPage));
            writeImages.Add(new SqlitePageImage(targetGrandparentPage, replacementGrandparentPage));
        }
        if (replacementRootPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(rootPage, existingRootPage));
            writeImages.Add(new SqlitePageImage(rootPage, replacementRootPage));
        }

        // The catalog page carries the WAL commit marker after every reachable page.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistValidatedTableInteriorArbitraryDepthLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var ownedPages = new HashSet<uint> { SchemaRootPage };
        if (rootPage < 2 || rootPage > sourcePageCount || !ownedPages.Add(rootPage))
            return false;

        var persistedCellIndex = 0;
        long? previousRowId = null;
        int? leafDepth = null;
        byte[]? sourceLeafPage = null;
        SqliteTableLeafPageView? sourceLeaf = null;
        uint targetLeafPage = 0;
        BoundedTableInteriorPathEntry[] targetPath = [];

        bool Visit(
            uint pageNumber,
            byte[] pageImage,
            List<BoundedTableInteriorPathEntry> path,
            int depth,
            out long maximumRowId)
        {
            maximumRowId = 0;
            switch (SqliteBtreePageHeader.Parse(pageImage).PageType)
            {
                case SqliteBtreePageType.TableLeaf:
                {
                    var leaf = SqliteTableLeafPageView.Parse(pageImage, _usableSpace);
                    if (leaf.Cells.Count == 0
                        || leaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                        || (leafDepth is { } expectedDepth && expectedDepth != depth))
                    {
                        return false;
                    }

                    leafDepth ??= depth;
                    foreach (var cell in leaf.Cells)
                    {
                        if (persistedCellIndex >= persistedCells.Count
                            || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                            || !cell.Cell.LocalPayload.Span.SequenceEqual(
                                persistedCells[persistedCellIndex].Record)
                            || (previousRowId is { } previous && cell.Cell.RowId <= previous))
                        {
                            return false;
                        }

                        persistedCellIndex++;
                        previousRowId = cell.Cell.RowId;
                    }

                    if (leaf.Search(change.RowId).IsExact)
                    {
                        if (sourceLeaf is not null)
                            return false;

                        sourceLeaf = leaf;
                        sourceLeafPage = pageImage;
                        targetLeafPage = pageNumber;
                        targetPath = path.ToArray();
                    }

                    maximumRowId = leaf.Cells[^1].Cell.RowId;
                    return true;
                }

                case SqliteBtreePageType.TableInterior:
                {
                    var interior = SqliteTableInteriorPageView.Parse(pageImage, _usableSpace);
                    if (interior.Cells.Count == 0)
                        return false;

                    var childPages = interior.Cells
                        .Select(cell => cell.Cell.LeftChildPage)
                        .Append(interior.Header.RightMostChildPage)
                        .ToArray();
                    if (childPages.Length != interior.Cells.Count + 1)
                        return false;

                    long? childMaximumRowId = null;
                    for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
                    {
                        var childPage = childPages[childIndex];
                        if (childPage < 2
                            || childPage > sourcePageCount
                            || !ownedPages.Add(childPage))
                        {
                            return false;
                        }

                        var childImage = _pager.ReadCommittedPage(childPage);
                        path.Add(new BoundedTableInteriorPathEntry(
                            pageNumber,
                            pageImage,
                            interior,
                            childIndex,
                            childPage));
                        if (!Visit(childPage, childImage, path, depth + 1, out var childMaximum))
                        {
                            path.RemoveAt(path.Count - 1);
                            return false;
                        }

                        path.RemoveAt(path.Count - 1);
                        if (childIndex < interior.Cells.Count
                            && interior.Cells[childIndex].Cell.RowId != childMaximum)
                        {
                            return false;
                        }

                        childMaximumRowId = childMaximum;
                    }

                    if (childMaximumRowId is null)
                        return false;

                    maximumRowId = childMaximumRowId.Value;
                    return true;
                }

                default:
                    return false;
            }
        }

        if (!Visit(rootPage, existingRootPage.ToArray(), [], depth: 1, out _)
            || persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || targetLeafPage == 0
            // The narrower paths above own roots with up to three interior levels.
            || targetPath.Length <= 3)
        {
            return false;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementSeparatorOwnerPage = null;
        BoundedTableInteriorPathEntry? separatorOwner = null;
        if (change.IsDelete && sourceLeaf.Cells[^1].Cell.RowId == change.RowId)
        {
            if (!TryFindValidatedTableInteriorSeparatorOwner(targetPath, out separatorOwner))
                return false;

            if (separatorOwner is not null
                && !TryReplaceTableInteriorSeparator(
                    separatorOwner.SourcePage,
                    separatorOwner.Page,
                    separatorOwner.ChildIndex,
                    separatorOwner.ChildPage,
                    replacementCells[^1].RowId,
                    out replacementSeparatorOwnerPage))
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (separatorOwner is not null)
        {
            sourcePages.Add(new SqlitePageImage(
                separatorOwner.PageNumber,
                separatorOwner.SourcePage));
            writeImages.Add(new SqlitePageImage(
                separatorOwner.PageNumber,
                replacementSeparatorOwnerPage!));
        }

        // Page one is the final WAL frame after every changed tree page.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private static bool TryFindValidatedTableInteriorSeparatorOwner(
        IReadOnlyList<BoundedTableInteriorPathEntry> path,
        out BoundedTableInteriorPathEntry? separatorOwner)
    {
        separatorOwner = null;
        for (var pathIndex = path.Count - 1; pathIndex >= 0; pathIndex--)
        {
            var candidate = path[pathIndex];
            if (candidate.ChildIndex < 0 || candidate.ChildIndex > candidate.Page.Cells.Count)
                return false;

            if (candidate.ChildIndex == candidate.Page.Cells.Count)
            {
                if (candidate.Page.Header.RightMostChildPage != candidate.ChildPage)
                    return false;

                continue;
            }

            if (candidate.Page.Cells[candidate.ChildIndex].Cell.LeftChildPage != candidate.ChildPage)
                return false;

            separatorOwner = candidate;
            return true;
        }

        return true;
    }

    private bool TryReplaceTableInteriorSeparator(
        ReadOnlySpan<byte> sourcePage,
        SqliteTableInteriorPageView parent,
        int separatorIndex,
        uint leftChildPage,
        long separatorRowId,
        out byte[] replacementPage)
    {
        replacementPage = null!;
        if (separatorIndex < 0
            || separatorIndex >= parent.Cells.Count
            || parent.Cells[separatorIndex].Cell.LeftChildPage != leftChildPage)
        {
            return false;
        }

        try
        {
            var builder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                parent.Header.RightMostChildPage);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                var cell = parent.Cells[cellIndex].Cell;
                builder.Append(cellIndex == separatorIndex
                    ? SqliteTableInteriorCell.Create(leftChildPage, separatorRowId)
                    : cell);
            }

            replacementPage = sourcePage.ToArray();
            builder.WriteTo(replacementPage);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryPersistBoundedTableInteriorRootSingleLeafMutation(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (!TryGetBoundedSingleLeafChange(
                tableName,
                table,
                persistedTable,
                out var persistedCells,
                out var change))
        {
            return false;
        }

        var parent = SqliteTableInteriorPageView.Parse(existingRootPage, _usableSpace);
        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var childPages = parent.Cells
            .Select(cell => cell.Cell.LeftChildPage)
            .Append(parent.Header.RightMostChildPage)
            .ToArray();
        if (childPages.Length == 0)
            return false;

        SqliteTableLeafPageView? sourceLeaf = null;
        byte[]? sourceLeafPage = null;
        uint targetLeafPage = 0;
        var targetChildIndex = -1;
        var persistedCellIndex = 0;
        long? previousMaximumRowId = null;
        for (var childIndex = 0; childIndex < childPages.Length; childIndex++)
        {
            var childPage = childPages[childIndex];
            if (childPage < 2
                || childPage == rootPage
                || childPage > sourcePageCount)
            {
                return false;
            }

            var childImage = _pager.ReadCommittedPage(childPage);
            if (SqliteBtreePageHeader.Parse(childImage).PageType != SqliteBtreePageType.TableLeaf)
                return false;

            var childLeaf = SqliteTableLeafPageView.Parse(childImage, _usableSpace);
            if (childLeaf.Cells.Count == 0
                || childLeaf.Cells.Any(cell => cell.Cell.FirstOverflowPage is not null)
                || (previousMaximumRowId is { } maximumRowId
                    && childLeaf.Cells[0].Cell.RowId <= maximumRowId))
            {
                return false;
            }

            if (childIndex < parent.Cells.Count
                && childLeaf.Cells[^1].Cell.RowId != parent.Cells[childIndex].Cell.RowId)
            {
                return false;
            }

            foreach (var cell in childLeaf.Cells)
            {
                if (persistedCellIndex >= persistedCells.Count
                    || cell.Cell.RowId != persistedCells[persistedCellIndex].RowId
                    || !cell.Cell.LocalPayload.Span.SequenceEqual(
                        persistedCells[persistedCellIndex].Record))
                {
                    return false;
                }

                persistedCellIndex++;
            }

            if (childLeaf.Search(change.RowId).IsExact)
            {
                if (sourceLeaf is not null)
                    return false;

                sourceLeaf = childLeaf;
                sourceLeafPage = childImage;
                targetLeafPage = childPage;
                targetChildIndex = childIndex;
            }

            previousMaximumRowId = childLeaf.Cells[^1].Cell.RowId;
        }

        if (persistedCellIndex != persistedCells.Count
            || sourceLeaf is null
            || sourceLeafPage is null
            || targetLeafPage == 0
            || targetChildIndex < 0)
        {
            return false;
        }

        if (change.IsDelete
            && TryPersistBoundedTableInteriorRootCollapse(
                rootPage,
                schemaPage,
                existingRootPage,
                currentHeader,
                parent,
                childPages,
                persistedCells,
                change))
        {
            return true;
        }

        if (change.IsDelete
            && sourceLeaf.Cells.Count == 1
            && TryPersistBoundedTableInteriorRootEmptyChildRemoval(
                rootPage,
                schemaPage,
                existingRootPage,
                currentHeader,
                parent,
                childPages,
                targetChildIndex,
                targetLeafPage,
                sourceLeafPage))
        {
            return true;
        }

        if (change.IsDelete && sourceLeaf.Cells.Count == 1)
            return false;

        var replacementCells = new List<SqliteTableLeafCell>(
            sourceLeaf.Cells.Count - (change.IsDelete ? 1 : 0));
        foreach (var sourceCell in sourceLeaf.Cells)
        {
            if (sourceCell.Cell.RowId != change.RowId)
            {
                replacementCells.Add(sourceCell.Cell);
                continue;
            }

            if (change.IsDelete)
                continue;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(
                    sourceCell.Cell.RowId,
                    change.ReplacementRecord!,
                    _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementLeafPage))
        {
            return false;
        }

        byte[]? replacementRootPage = null;
        if (change.IsDelete
            && targetChildIndex < parent.Cells.Count
            && parent.Cells[targetChildIndex].Cell.RowId == change.RowId)
        {
            try
            {
                var parentBuilder = new SqliteTableInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    parent.Header.RightMostChildPage);
                for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
                {
                    var cell = parent.Cells[cellIndex].Cell;
                    parentBuilder.Append(cellIndex == targetChildIndex
                        ? SqliteTableInteriorCell.Create(targetLeafPage, replacementCells[^1].RowId)
                        : cell);
                }

                replacementRootPage = existingRootPage.ToArray();
                parentBuilder.WriteTo(replacementRootPage);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(targetLeafPage, sourceLeafPage),
        };
        var writeImages = new List<SqlitePageImage>
        {
            new(targetLeafPage, replacementLeafPage),
        };
        if (replacementRootPage is not null)
        {
            sourcePages.Add(new SqlitePageImage(rootPage, existingRootPage));
            writeImages.Add(new SqlitePageImage(rootPage, replacementRootPage));
        }

        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));
        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            sourcePageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootEmptyChildRemoval(
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader,
        SqliteTableInteriorPageView parent,
        IReadOnlyList<uint> childPages,
        int targetChildIndex,
        uint targetLeafPage,
        ReadOnlySpan<byte> sourceLeafPage)
    {
        if (parent.Cells.Count < 2
            || childPages.Count != parent.Cells.Count + 1
            || childPages.Count < 3
            || targetChildIndex < 0
            || targetChildIndex >= childPages.Count
            || childPages[targetChildIndex] != targetLeafPage)
        {
            return false;
        }

        byte[] replacementRootPage;
        try
        {
            var removedSeparatorIndex = targetChildIndex == parent.Cells.Count
                ? parent.Cells.Count - 1
                : targetChildIndex;
            var rightMostChildPage = targetChildIndex == parent.Cells.Count
                ? parent.Cells[^1].Cell.LeftChildPage
                : parent.Header.RightMostChildPage;
            var parentBuilder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                rightMostChildPage);
            for (var cellIndex = 0; cellIndex < parent.Cells.Count; cellIndex++)
            {
                if (cellIndex != removedSeparatorIndex)
                    parentBuilder.Append(parent.Cells[cellIndex].Cell);
            }

            replacementRootPage = existingRootPage.ToArray();
            parentBuilder.WriteTo(replacementRootPage);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        SqliteFreelist retiredChild;
        try
        {
            retiredChild = SqliteFreelist.CreateFromFreePages(
                currentHeader.DatabaseSizeInPages,
                [targetLeafPage],
                _pageSize,
                _usableSpace);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (retiredChild.PageCount != 1
            || retiredChild.FirstTrunkPage != targetLeafPage
            || retiredChild.PageNumbers.Count != 1
            || retiredChild.PageNumbers[0] != targetLeafPage
            || retiredChild.TrunkPageNumbers.Count != 1
            || retiredChild.TrunkPageNumbers[0] != targetLeafPage
            || retiredChild.PageImages.Count != 1
            || retiredChild.PageImages[0].PageNumber != targetLeafPage)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            FirstFreelistTrunkPage = retiredChild.FirstTrunkPage,
            FreelistPageCount = retiredChild.PageCount,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            currentHeader.DatabaseSizeInPages,
            currentHeader.DatabaseSizeInPages,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(rootPage, existingRootPage),
                new SqlitePageImage(targetLeafPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(rootPage, replacementRootPage),
                retiredChild.PageImages[0],
                // Page one publishes both the replacement root and retired child.
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedTableInteriorRootCollapse(
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader,
        SqliteTableInteriorPageView parent,
        IReadOnlyList<uint> childPages,
        IReadOnlyList<(long RowId, byte[] Record)> persistedCells,
        BoundedSingleLeafChange change)
    {
        if (!change.IsDelete
            || parent.Cells.Count != 1
            || childPages.Count != 2
            || persistedCells.Count < 2)
        {
            return false;
        }

        var replacementCells = new List<SqliteTableLeafCell>(persistedCells.Count - 1);
        foreach (var (rowId, record) in persistedCells)
        {
            if (rowId == change.RowId)
                continue;

            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
                return false;

            try
            {
                replacementCells.Add(SqliteTableLeafCell.Create(rowId, record, _usableSpace));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (replacementCells.Count != persistedCells.Count - 1
            || replacementCells.Count == 0
            || !TryBuildBoundedTableLeafPage(
                replacementCells,
                0,
                replacementCells.Count,
                out var replacementRootPage))
        {
            return false;
        }

        SqliteFreelist retiredChildren;
        try
        {
            retiredChildren = SqliteFreelist.CreateFromFreePages(
                currentHeader.DatabaseSizeInPages,
                childPages,
                _pageSize,
                _usableSpace);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (retiredChildren.PageCount != childPages.Count
            || !retiredChildren.PageNumbers.SequenceEqual(childPages.Order())
            || retiredChildren.PageImages.Count != childPages.Count)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
            FirstFreelistTrunkPage = retiredChildren.FirstTrunkPage,
            FreelistPageCount = retiredChildren.PageCount,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(2 + childPages.Count)
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(rootPage, existingRootPage),
        };
        sourcePages.AddRange(childPages.Select(
            childPage => new SqlitePageImage(childPage, _pager.ReadCommittedPage(childPage))));

        var writeImages = new List<SqlitePageImage>(2 + retiredChildren.PageImages.Count)
        {
            new(rootPage, replacementRootPage),
        };
        writeImages.AddRange(retiredChildren.PageImages.OrderBy(image => image.PageNumber));
        // Page one publishes both the leaf root and its retired-child freelist.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            currentHeader.DatabaseSizeInPages,
            currentHeader.DatabaseSizeInPages,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool CommitBoundedTableInteriorRootLeafMutation(
        uint leafPage,
        ReadOnlySpan<byte> sourceLeafPage,
        ReadOnlySpan<byte> replacementLeafPage,
        ReadOnlySpan<byte> schemaPage,
        SqliteDatabaseHeader currentHeader)
    {
        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            currentHeader.DatabaseSizeInPages,
            currentHeader.DatabaseSizeInPages,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(leafPage, sourceLeafPage),
            ],
            [
                new SqlitePageImage(leafPage, replacementLeafPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryGetBoundedSingleLeafChange(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        out IReadOnlyList<(long RowId, byte[] Record)> persistedCells,
        out BoundedSingleLeafChange change)
    {
        persistedCells = EnumerateRowCells(tableName, persistedTable).ToArray();
        change = null!;
        var targetCells = EnumerateRowCells(tableName, table).ToArray();
        if (targetCells.Length == persistedCells.Count)
        {
            for (var index = 0; index < persistedCells.Count; index++)
            {
                if (targetCells[index].RowId != persistedCells[index].RowId)
                    return false;
                if (targetCells[index].Record.AsSpan().SequenceEqual(persistedCells[index].Record))
                    continue;
                if (change is not null)
                    return false;

                var layout = SqlitePayloadLayout.Calculate(
                    SqliteBtreePageType.TableLeaf,
                    checked((ulong)targetCells[index].Record.Length),
                    _usableSpace);
                if (layout.UsesOverflow)
                    return false;

                change = new BoundedSingleLeafChange(
                    targetCells[index].RowId,
                    targetCells[index].Record,
                    IsDelete: false);
            }

            return change is not null;
        }

        if (targetCells.Length != persistedCells.Count - 1)
            return false;

        var persistedIndex = 0;
        for (var targetIndex = 0; targetIndex < targetCells.Length; targetIndex++, persistedIndex++)
        {
            if (targetCells[targetIndex].RowId == persistedCells[persistedIndex].RowId)
            {
                if (!targetCells[targetIndex].Record.AsSpan().SequenceEqual(persistedCells[persistedIndex].Record))
                    return false;
                continue;
            }

            if (change is not null
                || targetCells[targetIndex].RowId <= persistedCells[persistedIndex].RowId)
            {
                return false;
            }

            change = new BoundedSingleLeafChange(
                persistedCells[persistedIndex].RowId,
                ReplacementRecord: null,
                IsDelete: true);
            persistedIndex++;
            if (persistedIndex >= persistedCells.Count
                || targetCells[targetIndex].RowId != persistedCells[persistedIndex].RowId
                || !targetCells[targetIndex].Record.AsSpan().SequenceEqual(persistedCells[persistedIndex].Record))
            {
                return false;
            }
        }

        if (persistedIndex < persistedCells.Count)
        {
            if (change is not null || persistedIndex != persistedCells.Count - 1)
                return false;

            change = new BoundedSingleLeafChange(
                persistedCells[persistedIndex].RowId,
                ReplacementRecord: null,
                IsDelete: true);
            persistedIndex++;
        }

        return persistedIndex == persistedCells.Count && change is not null;
    }

    private bool TryGetBoundedStrictAppendCell(
        string tableName,
        EmbeddedTable table,
        EmbeddedTable persistedTable,
        out IReadOnlyList<(long RowId, byte[] Record)> persistedCells,
        out SqliteTableLeafCell appendedCell)
    {
        persistedCells = EnumerateRowCells(tableName, persistedTable).ToArray();
        appendedCell = null!;
        var targetCells = EnumerateRowCells(tableName, table).ToArray();
        if (targetCells.Length != persistedCells.Count + 1)
            return false;

        for (var index = 0; index < persistedCells.Count; index++)
        {
            if (targetCells[index].RowId != persistedCells[index].RowId
                || !targetCells[index].Record.AsSpan().SequenceEqual(persistedCells[index].Record))
            {
                return false;
            }
        }

        if (persistedCells.Count != 0
            && targetCells[^1].RowId <= persistedCells[^1].RowId)
        {
            return false;
        }

        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)targetCells[^1].Record.Length),
            _usableSpace);
        if (layout.UsesOverflow)
            return false;

        try
        {
            appendedCell = SqliteTableLeafCell.Create(
                targetCells[^1].RowId,
                targetCells[^1].Record,
                _usableSpace);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryPersistBoundedTableRootLeafPromotion(
        string tableName,
        EmbeddedTable table,
        uint rootPage,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingRootPage,
        SqliteDatabaseHeader currentHeader)
    {
        if (currentHeader.DatabaseSizeInPages > uint.MaxValue - 3)
            return false;

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var leftChildPage = sourcePageCount + 1;
        var rightChildPage = sourcePageCount + 2;
        if (!TryBuildBoundedTableLeafSplitImages(
                tableName,
                table,
                out var leftPage,
                out var rightPage,
                out var separatorRowId))
        {
            return false;
        }

        byte[] promotedRootPage;
        try
        {
            var rootBuilder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                rightChildPage);
            rootBuilder.Append(SqliteTableInteriorCell.Create(leftChildPage, separatorRowId));
            promotedRootPage = existingRootPage.ToArray();
            rootBuilder.WriteTo(promotedRootPage);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = rightChildPage,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            rightChildPage,
            _pageSize,
            [
                new SqlitePageImage(SchemaRootPage, sourceSchemaPage),
                new SqlitePageImage(rootPage, existingRootPage),
            ],
            [
                new SqlitePageImage(leftChildPage, leftPage),
                new SqlitePageImage(rightChildPage, rightPage),
                new SqlitePageImage(rootPage, promotedRootPage),
                new SqlitePageImage(SchemaRootPage, targetSchemaPage),
            ]);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private bool TryPersistBoundedSecondaryIndexRootLeafPromotions(
        uint tableRootPage,
        ReadOnlySpan<byte> replacementTablePage,
        IReadOnlyList<(uint PageNumber, byte[] SourcePage, byte[] ReplacementPage)> indexReplacementPages,
        IReadOnlyList<BoundedIndexRootLeafPromotion> indexRootPromotions,
        ReadOnlySpan<byte> schemaPage,
        ReadOnlySpan<byte> existingTablePage,
        SqliteDatabaseHeader currentHeader)
    {
        if (indexRootPromotions.Count == 0)
            throw new ArgumentException("At least one index root must be promoted.", nameof(indexRootPromotions));

        var sourcePageCount = currentHeader.DatabaseSizeInPages;
        var promotionCount = checked((uint)indexRootPromotions.Count);
        if (promotionCount > (uint.MaxValue - sourcePageCount) / 2)
            return false;

        var targetPageCount = checked(sourcePageCount + (promotionCount * 2));
        var appendedLeafPages = new List<SqlitePageImage>(checked(indexRootPromotions.Count * 2));
        var promotedRootPages = new List<SqlitePageImage>(indexRootPromotions.Count);
        for (var promotionIndex = 0; promotionIndex < indexRootPromotions.Count; promotionIndex++)
        {
            var promotion = indexRootPromotions[promotionIndex];
            var leftChildPage = checked(sourcePageCount + ((uint)promotionIndex * 2) + 1);
            var rightChildPage = checked(leftChildPage + 1);
            try
            {
                var comparer = new SqliteIndexRecordComparer(_textEncoding);
                var rootBuilder = new SqliteIndexInteriorPageBuilder(
                    _pageSize,
                    _usableSpace,
                    rightChildPage,
                    comparer);
                rootBuilder.Append(
                    SqliteIndexInteriorCell.Create(
                        leftChildPage,
                        promotion.SeparatorRecord,
                        _usableSpace),
                    promotion.SeparatorRecord);
                var promotedRootPage = promotion.SourceRootPage.ToArray();
                rootBuilder.WriteTo(promotedRootPage);
                appendedLeafPages.Add(new SqlitePageImage(leftChildPage, promotion.LeftPage));
                appendedLeafPages.Add(new SqlitePageImage(rightChildPage, promotion.RightPage));
                promotedRootPages.Add(new SqlitePageImage(promotion.RootPage, promotedRootPage));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        var sourceSchemaPage = schemaPage.ToArray();
        var targetSchemaPage = schemaPage.ToArray();
        var newChangeCounter = currentHeader.ChangeCounter + 1;
        var newHeader = currentHeader with
        {
            ChangeCounter = newChangeCounter,
            DatabaseSizeInPages = targetPageCount,
            VersionValidFor = newChangeCounter,
        };
        newHeader.WriteTo(targetSchemaPage);

        var sourcePages = new List<SqlitePageImage>(
            checked(2 + indexReplacementPages.Count + indexRootPromotions.Count))
        {
            new(SchemaRootPage, sourceSchemaPage),
            new(tableRootPage, existingTablePage),
        };
        sourcePages.AddRange(indexReplacementPages.Select(
            replacement => new SqlitePageImage(replacement.PageNumber, replacement.SourcePage)));
        sourcePages.AddRange(indexRootPromotions.Select(
            promotion => new SqlitePageImage(promotion.RootPage, promotion.SourceRootPage)));

        var writeImages = new List<SqlitePageImage>(
            checked(appendedLeafPages.Count + 2 + indexReplacementPages.Count + promotedRootPages.Count));
        writeImages.AddRange(appendedLeafPages);
        writeImages.Add(new SqlitePageImage(tableRootPage, replacementTablePage));
        writeImages.AddRange(indexReplacementPages
            .OrderBy(replacement => replacement.PageNumber)
            .Select(replacement => new SqlitePageImage(
                replacement.PageNumber,
                replacement.ReplacementPage)));
        writeImages.AddRange(promotedRootPages.OrderBy(image => image.PageNumber));
        // Page one is the final routing/catalog image and therefore carries the
        // transaction's WAL commit marker after every tree image is durable.
        writeImages.Add(new SqlitePageImage(SchemaRootPage, targetSchemaPage));

        var mutation = new SqliteBtreeSplitMutation(
            sourcePageCount,
            targetPageCount,
            _pageSize,
            sourcePages,
            writeImages);
        mutation.CommitTo(_pager);
        CheckpointCommittedMutation(reclaimTrailingPages: false);
        _header = newHeader;
        return true;
    }

    private sealed record BoundedIndexRootLeafPromotion(
        uint RootPage,
        byte[] SourceRootPage,
        byte[] LeftPage,
        byte[] RightPage,
        byte[] SeparatorRecord);

    private sealed record BoundedSecondaryIndexInteriorLeafDeletion(
        uint LeafPageNumber,
        byte[] ReplacementLeafPage,
        IReadOnlyList<SqlitePageImage> SourceTreePages);

    private sealed record BoundedSecondaryIndexInteriorLeafInsertion(
        uint LeafPageNumber,
        byte[] ReplacementLeafPage,
        IReadOnlyList<SqlitePageImage> SourceTreePages);

    private sealed record BoundedSingleLeafChange(
        long RowId,
        byte[]? ReplacementRecord,
        bool IsDelete);

    private sealed record BoundedTableInteriorPathEntry(
        uint PageNumber,
        byte[] SourcePage,
        SqliteTableInteriorPageView Page,
        int ChildIndex,
        uint ChildPage);

    private static bool IsBoundedIndexLeafMutationCompatible(IndexDefinition definition)
        => definition.Index.Columns.Count != 0
           && definition.Index.Columns.All(column =>
               !column.Descending
               && (column.Collation is null
                   || string.Equals(column.Collation, "BINARY", StringComparison.OrdinalIgnoreCase)));

    private bool HasCurrentSchemaShape(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers)
    {
        if (tables.Count != _tableRootPages.Count
            || tables.Keys.Any(name => !_tableRootPages.ContainsKey(name)))
        {
            return false;
        }

        var tableNames = tables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var indexes = GetIndexDefinitions(tableNames, tables, views, triggers);
        if (indexes.Count != _indexRootPages.Count
            || indexes.Any(index => !_indexRootPages.ContainsKey(index.Index.Name)))
        {
            return false;
        }

        var entries = BuildSchemaEntries(tables, views, triggers, _tableRootPages, _indexRootPages);
        return string.Equals(
            ComputeSchemaSignature(entries),
            _lastSchemaSignature,
            StringComparison.Ordinal);
    }

    private static bool TryGetSingleChangedTable(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, EmbeddedTable> persistedTables,
        out string tableName,
        out EmbeddedTable table)
    {
        tableName = string.Empty;
        table = null!;
        if (tables.Count != persistedTables.Count)
            return false;

        foreach (var (name, candidate) in tables)
        {
            if (!persistedTables.TryGetValue(name, out var persisted))
                return false;
            if (HaveSameRows(candidate, persisted))
                continue;
            if (table is not null)
                return false;

            tableName = name;
            table = candidate;
        }

        return table is not null;
    }

    private static bool HaveSameRows(EmbeddedTable left, EmbeddedTable right)
    {
        if (left.Rows.Count != right.Rows.Count || left.RowIds.Count != right.RowIds.Count)
            return false;

        for (var rowIndex = 0; rowIndex < left.Rows.Count; rowIndex++)
        {
            if (left.RowIds[rowIndex] != right.RowIds[rowIndex]
                || !left.Rows[rowIndex].AsSpan().SequenceEqual(right.Rows[rowIndex]))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryBuildBoundedTableLeafImage(
        string tableName,
        EmbeddedTable table,
        ReadOnlySpan<byte> existingPage,
        out byte[] replacementPage)
    {
        var builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage: false);
        foreach (var (rowId, record) in EnumerateRowCells(tableName, table))
        {
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
            {
                replacementPage = null!;
                return false;
            }

            try
            {
                builder.Append(SqliteTableLeafCell.Create(rowId, record, _usableSpace));
            }
            catch (InvalidOperationException)
            {
                replacementPage = null!;
                return false;
            }
        }

        replacementPage = existingPage.ToArray();
        builder.WriteTo(replacementPage);
        return true;
    }

    private bool TryBuildBoundedTableLeafSplitImages(
        string tableName,
        EmbeddedTable table,
        out byte[] leftPage,
        out byte[] rightPage,
        out long separatorRowId)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRowId = 0;
        var cells = new List<SqliteTableLeafCell>(table.Rows.Count);
        foreach (var (rowId, record) in EnumerateRowCells(tableName, table))
        {
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.TableLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
                return false;

            try
            {
                cells.Add(SqliteTableLeafCell.Create(rowId, record, _usableSpace));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return TryBuildBoundedTableLeafSplitImages(
            cells,
            out leftPage,
            out rightPage,
            out separatorRowId);
    }

    private bool TryBuildBoundedTableLeafSplitImages(
        IReadOnlyList<SqliteTableLeafCell> cells,
        out byte[] leftPage,
        out byte[] rightPage,
        out long separatorRowId)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRowId = 0;
        if (cells.Count < 2)
            return false;

        for (var leftCellCount = 1; leftCellCount < cells.Count; leftCellCount++)
        {
            if (!TryBuildBoundedTableLeafPage(cells, 0, leftCellCount, out leftPage)
                || !TryBuildBoundedTableLeafPage(
                    cells,
                    leftCellCount,
                    cells.Count - leftCellCount,
                    out rightPage))
            {
                continue;
            }

            separatorRowId = cells[leftCellCount - 1].RowId;
            return true;
        }

        leftPage = null!;
        rightPage = null!;
        return false;
    }

    private bool TryBuildBoundedTableLeafPage(
        IReadOnlyList<SqliteTableLeafCell> cells,
        int start,
        int count,
        out byte[] page)
    {
        try
        {
            var builder = new SqliteTableLeafPageBuilder(
                _pageSize,
                _usableSpace,
                isFirstPage: false);
            for (var index = start; index < start + count; index++)
                builder.Append(cells[index]);

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private bool TryBuildBoundedIndexLeafImage(
        IndexDefinition definition,
        ReadOnlySpan<byte> existingPage,
        out byte[] replacementPage)
    {
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var records = BuildIndexRecords(
            definition.TableName,
            definition.Table,
            definition.Index,
            comparer);
        ValidateBoundedUniqueIndexRecords(definition, records, comparer);

        var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
        foreach (var record in records)
        {
            var layout = SqlitePayloadLayout.Calculate(
                SqliteBtreePageType.IndexLeaf,
                checked((ulong)record.Length),
                _usableSpace);
            if (layout.UsesOverflow)
            {
                replacementPage = null!;
                return false;
            }

            try
            {
                builder.Append(SqliteIndexLeafCell.Create(record, _usableSpace), record);
            }
            catch (InvalidOperationException)
            {
                replacementPage = null!;
                return false;
            }
        }

        replacementPage = existingPage.ToArray();
        builder.WriteTo(replacementPage);
        return true;
    }

    private bool TryBuildBoundedIndexRootLeafSplitImages(
        IReadOnlyList<byte[]> records,
        out byte[] leftPage,
        out byte[] rightPage,
        out byte[] separatorRecord)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRecord = null!;
        if (records.Count < 3)
            return false;

        foreach (var record in records)
        {
            if (SqlitePayloadLayout.Calculate(
                    SqliteBtreePageType.IndexLeaf,
                    checked((ulong)record.Length),
                    _usableSpace).UsesOverflow)
            {
                return false;
            }
        }

        var middle = records.Count / 2;
        for (var distance = 0; distance < records.Count; distance++)
        {
            var beforeMiddle = middle - distance;
            if (TryBuildBoundedIndexRootLeafSplitAt(
                    records,
                    beforeMiddle,
                    out leftPage,
                    out rightPage,
                    out separatorRecord))
            {
                return true;
            }

            var afterMiddle = middle + distance;
            if (afterMiddle != beforeMiddle
                && TryBuildBoundedIndexRootLeafSplitAt(
                    records,
                    afterMiddle,
                    out leftPage,
                    out rightPage,
                    out separatorRecord))
            {
                return true;
            }
        }

        leftPage = null!;
        rightPage = null!;
        separatorRecord = null!;
        return false;
    }

    private bool TryBuildBoundedIndexRootLeafSplitAt(
        IReadOnlyList<byte[]> records,
        int separatorIndex,
        out byte[] leftPage,
        out byte[] rightPage,
        out byte[] separatorRecord)
    {
        leftPage = null!;
        rightPage = null!;
        separatorRecord = null!;
        if (separatorIndex <= 0 || separatorIndex >= records.Count - 1
            || !TryBuildBoundedIndexLeafPage(records, 0, separatorIndex, out leftPage)
            || !TryBuildBoundedIndexLeafPage(
                records,
                separatorIndex + 1,
                records.Count - separatorIndex - 1,
                out rightPage))
        {
            return false;
        }

        separatorRecord = records[separatorIndex].ToArray();
        return true;
    }

    private bool TryBuildBoundedIndexLeafPage(
        IReadOnlyList<byte[]> records,
        int start,
        int count,
        out byte[] page)
    {
        try
        {
            var comparer = new SqliteIndexRecordComparer(_textEncoding);
            var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
            for (var index = start; index < start + count; index++)
            {
                var record = records[index];
                builder.Append(SqliteIndexLeafCell.Create(record, _usableSpace), record);
            }

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private bool TryBuildBoundedIndexLeafReplacementPage(
        IReadOnlyList<byte[]> records,
        ReadOnlySpan<byte> sourcePage,
        out byte[] replacementPage)
    {
        try
        {
            var comparer = new SqliteIndexRecordComparer(_textEncoding);
            var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
            foreach (var record in records)
            {
                if (SqlitePayloadLayout.Calculate(
                        SqliteBtreePageType.IndexLeaf,
                        checked((ulong)record.Length),
                        _usableSpace).UsesOverflow)
                {
                    replacementPage = null!;
                    return false;
                }

                builder.Append(SqliteIndexLeafCell.Create(record, _usableSpace), record);
            }

            replacementPage = sourcePage.ToArray();
            builder.WriteTo(replacementPage);
            return true;
        }
        catch (ArgumentException)
        {
            replacementPage = null!;
            return false;
        }
        catch (InvalidOperationException)
        {
            replacementPage = null!;
            return false;
        }
    }

    private void ValidateBoundedUniqueIndexRecords(
        IndexDefinition definition,
        IReadOnlyList<byte[]> records,
        SqliteIndexRecordComparer comparer)
    {
        if (!definition.Index.Unique)
            return;

        SqlValue[]? previousNonNullKey = null;
        foreach (var record in records)
        {
            var values = SqliteRecordCodec.Decode(record, _textEncoding);
            if (values.Length != definition.Index.Columns.Count + 1)
            {
                throw new InvalidDataException(
                    $"SQLite index '{definition.Index.Name}' record has an unexpected column count.");
            }

            var key = values[..^1];
            if (key.Any(value => value.Kind == SqlValueKind.Null))
                continue;

            if (previousNonNullKey is not null
                && comparer.Compare(previousNonNullKey, key) == 0)
            {
                var columns = definition.Index.Columns.Select(
                    column => $"{definition.TableName}.{column.Name}");
                throw new EmbeddedSqlException($"UNIQUE constraint failed: {string.Join(", ", columns)}");
            }

            previousNonNullKey = key;
        }
    }

    private void CheckpointCommittedMutation(bool reclaimTrailingPages)
    {
        try
        {
            _pager.CheckpointToMainStoreAndResetWal();
        }
        catch (IOException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (UnauthorizedAccessException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (InvalidDataException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (InvalidOperationException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
        catch (NotSupportedException exception) when (!reclaimTrailingPages)
        {
            throw RecordPostCommitMaintenanceFailure(exception);
        }
    }

    private static void ValidateRewritePlan(
        uint targetPageCount,
        PreparedSchemaTree schemaTree,
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, uint> tableRootPages,
        IReadOnlyDictionary<uint, PreparedTableTree> tablePages,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, uint> indexRootPages,
        IReadOnlyDictionary<uint, PreparedIndexTree> indexPages,
        SqliteFreelist freelist)
    {
        var activePages = CollectRewriteActivePages(
            schemaTree,
            tableNames,
            tableRootPages,
            tablePages,
            indexes,
            indexRootPages,
            indexPages);

        foreach (var activePage in activePages)
        {
            if (activePage == 0 || activePage > targetPageCount)
            {
                throw new InvalidOperationException(
                    $"Managed file rewrite active page {activePage} is outside its target range 1..{targetPageCount}.");
            }
        }

        var accountedPages = new HashSet<uint>(activePages);
        foreach (var freePage in freelist.PageNumbers)
        {
            if (freePage < 2 || freePage > targetPageCount)
            {
                throw new InvalidOperationException(
                    $"Managed file rewrite freelist page {freePage} is outside its target range 2..{targetPageCount}.");
            }
            if (!accountedPages.Add(freePage))
                throw new InvalidOperationException($"Managed file rewrite assigns page {freePage} more than once.");
        }

        if (accountedPages.Count != targetPageCount)
        {
            throw new InvalidOperationException(
                $"Managed file rewrite accounts for {accountedPages.Count} pages, but its committed size is {targetPageCount}.");
        }

        var imagePages = new HashSet<uint>();
        foreach (var image in freelist.PageImages)
        {
            if (!imagePages.Add(image.PageNumber)
                || !freelist.PageNumbers.Contains(image.PageNumber))
            {
                throw new InvalidOperationException(
                    $"Managed file rewrite freelist image for page {image.PageNumber} is invalid.");
            }
        }
        if (imagePages.Count != freelist.PageCount)
            throw new InvalidOperationException("Managed file rewrite did not materialize every freelist page.");
    }

    private static HashSet<uint> CollectRewriteActivePages(
        PreparedSchemaTree schemaTree,
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, uint> tableRootPages,
        IReadOnlyDictionary<uint, PreparedTableTree> tablePages,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, uint> indexRootPages,
        IReadOnlyDictionary<uint, PreparedIndexTree> indexPages)
    {
        var activePages = new HashSet<uint> { SchemaRootPage };
        foreach (var interiorPage in schemaTree.InteriorPages)
            AddActivePage(activePages, interiorPage.PageNumber);
        foreach (var leafPage in schemaTree.LeafPages)
            AddActivePage(activePages, leafPage.PageNumber);
        foreach (var name in tableNames)
        {
            var rootPage = tableRootPages[name];
            AddActivePage(activePages, rootPage);
            var tree = tablePages[rootPage];
            foreach (var interiorPage in tree.InteriorPages)
                AddActivePage(activePages, interiorPage.PageNumber);
            foreach (var leafPage in tree.LeafPages)
                AddActivePage(activePages, leafPage.PageNumber);
            foreach (var overflowPage in tree.OverflowPages)
                AddActivePage(activePages, overflowPage.PageNumber);
        }

        foreach (var definition in indexes)
        {
            var rootPage = indexRootPages[definition.Index.Name];
            AddActivePage(activePages, rootPage);
            var tree = indexPages[rootPage];
            foreach (var interiorPage in tree.InteriorPages)
                AddActivePage(activePages, interiorPage.PageNumber);
            foreach (var leafPage in tree.LeafPages)
                AddActivePage(activePages, leafPage.PageNumber);
            foreach (var overflowPage in tree.OverflowPages)
                AddActivePage(activePages, overflowPage.PageNumber);
        }

        return activePages;
    }

    private static IEnumerable<uint> EnumerateFreePages(uint targetPageCount, ISet<uint> activePages)
    {
        for (var pageNumber = 2U; pageNumber <= targetPageCount; pageNumber++)
        {
            if (!activePages.Contains(pageNumber))
                yield return pageNumber;
            if (pageNumber == uint.MaxValue)
                yield break;
        }
    }

    private static void AddActivePage(ISet<uint> activePages, uint pageNumber)
    {
        if (pageNumber == 0)
            throw new InvalidOperationException("Managed file rewrite cannot assign SQLite page zero.");
        if (!activePages.Add(pageNumber))
            throw new InvalidOperationException($"Managed file rewrite assigns active page {pageNumber} more than once.");
    }

    /// <summary>Flushes the committed view into the main file and releases resources.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pager.Dispose();
    }

    private EmbeddedPostCommitMaintenanceException RecordPostCommitMaintenanceFailure(Exception exception)
    {
        _postCommitMaintenanceFailure = exception;
        return new EmbeddedPostCommitMaintenanceException(exception);
    }

    private void ThrowIfPostCommitMaintenanceFaulted()
    {
        if (_postCommitMaintenanceFailure is not null)
        {
            throw new InvalidOperationException(
                "A prior managed database mutation committed successfully, but post-commit checkpoint maintenance failed. "
                + "Dispose and reopen the database before another write.",
                _postCommitMaintenanceFailure);
        }
    }

    private static void ValidateTableRepresentable(string name, EmbeddedTable table)
    {
        if (table.WithoutRowid)
        {
            _ = ValidateWithoutRowidTableRepresentable(name, table);
            return;
        }

        // A table-level PRIMARY KEY(...) is backed by a separate unique index b-tree in
        // SQLite (unlike a column-level INTEGER PRIMARY KEY rowid alias), so it cannot be
        // persisted honestly here.
        if (table.TableLevelPrimaryKey is not null)
        {
            ValidatePrimaryKeyIndexPrerequisites(name, table, "a table-level PRIMARY KEY");
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{name}' because its table-level PRIMARY KEY requires an on-disk index b-tree.");
        }

        // A VIRTUAL generated column has no stored value in SQLite's record format; writing
        // its computed value would produce records a real SQLite library would misread, so
        // only STORED generated columns (which are physically stored) can be persisted.
        var virtualGenerated = Array.FindIndex(table.ColumnDefinitions, column => column.IsGenerated && !column.GeneratedStored);
        if (virtualGenerated >= 0)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{name}' because column '{table.ColumnDefinitions[virtualGenerated].Name}' is a VIRTUAL generated column, whose value is not stored in the SQLite record format; declare it STORED to persist it.");
        }

        var columns = table.ColumnDefinitions;
        var primaryKeyCount = 0;
        var primaryKeyIndex = -1;
        for (var index = 0; index < columns.Length; index++)
        {
            if (columns[index].Unique)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist table '{name}' because column '{columns[index].Name}' has a UNIQUE constraint, which requires an on-disk index b-tree.");
            }

            if (columns[index].PrimaryKey)
            {
                primaryKeyCount++;
                primaryKeyIndex = index;
            }
        }

        if (primaryKeyCount > 1)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{name}' because it has a composite PRIMARY KEY, which requires an on-disk index b-tree.");
        }

        if (primaryKeyCount == 1)
        {
            // Only a single-column INTEGER PRIMARY KEY that aliases the rowid can be
            // persisted without an index b-tree. A non-integer PRIMARY KEY or an
            // INTEGER PRIMARY KEY DESC (which SQLite backs with a separate unique index)
            // is rejected here rather than written as an invalid database.
            if (table.RowidAliasColumnIndex < 0)
            {
                ValidatePrimaryKeyIndexPrerequisites(name, table, "a non-rowid-alias PRIMARY KEY");
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist table '{name}' because its PRIMARY KEY column '{columns[primaryKeyIndex].Name}' is not an INTEGER PRIMARY KEY rowid alias; such a PRIMARY KEY requires an on-disk index b-tree.");
            }

            var seen = new HashSet<long>();
            foreach (var row in table.Rows)
            {
                var value = row[primaryKeyIndex];
                if (value.Kind != SqlValueKind.Integer)
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist table '{name}' because its INTEGER PRIMARY KEY column '{columns[primaryKeyIndex].Name}' contains a non-integer value; rowid aliases must be distinct non-null integers.");
                }

                if (!seen.Add(value.AsInteger()))
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist table '{name}' because its INTEGER PRIMARY KEY column '{columns[primaryKeyIndex].Name}' contains duplicate values.");
                }
            }
        }

        foreach (var index in table.Indexes)
            ValidateIndexRepresentable(name, table, index);
    }

    private static void ValidatePrimaryKeyIndexPrerequisites(
        string tableName,
        EmbeddedTable table,
        string primaryKeyKind)
    {
        var primaryKeySchema = table.PrimaryKeySchema;
        if (primaryKeySchema is null)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{tableName}' because its primary-key descriptor is missing.");
        }

        try
        {
            primaryKeySchema.EnsureSupportedByBinaryAscendingIndexWriter();
        }
        catch (ArgumentException exception)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist table '{tableName}' because its primary-key metadata is inconsistent.",
                exception);
        }
        catch (NotSupportedException exception)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist {primaryKeyKind} table '{tableName}' because {exception.Message} "
                + "The managed primary-key index writer supports only ascending BINARY terms.",
                exception);
        }
    }

    private static SqlitePrimaryKeySchema ValidateWithoutRowidTableRepresentable(
        string tableName,
        EmbeddedTable table)
    {
        ValidatePrimaryKeyIndexPrerequisites(tableName, table, "WITHOUT ROWID");
        var primaryKeySchema = table.PrimaryKeySchema
            ?? throw new InvalidOperationException("Validated WITHOUT ROWID table is missing its primary-key schema.");
        if (primaryKeySchema.Terms.Count != 1 || table.PrimaryKeyColumns.Count != 1)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because only one ascending BINARY primary-key column is supported.");
        }

        var term = primaryKeySchema.Terms[0];
        var primaryKeyColumn = table.PrimaryKeyColumns[0];
        if (term.ColumnIndex != primaryKeyColumn.Index
            || primaryKeyColumn.Descending
            || term.ColumnIndex < 0
            || term.ColumnIndex >= table.ColumnDefinitions.Length
            || !string.Equals(
                term.ColumnName,
                table.ColumnDefinitions[term.ColumnIndex].Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary-key metadata is inconsistent.");
        }

        foreach (var column in table.ColumnDefinitions)
        {
            if (column.Unique)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because column '{column.Name}' has a UNIQUE constraint, which requires an additional on-disk index b-tree.");
            }
            if (column.IsGenerated && !column.GeneratedStored)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because column '{column.Name}' is a VIRTUAL generated column, whose value is not stored in the SQLite record format; declare it STORED to persist it.");
            }
        }

        if (table.Indexes.Count != 0)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because secondary indexes require a primary-key-aware index writer that is not implemented.");
        }

        return primaryKeySchema;
    }

    private static void ValidateSchemaDefinitions(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers)
    {
        foreach (var (catalogName, view) in views)
            ValidateViewDefinition(catalogName, view);

        foreach (var (catalogName, trigger) in triggers)
            ValidateTriggerDefinition(catalogName, trigger, tables);
    }

    private static void ValidateViewDefinition(string catalogName, ViewDefinition view)
    {
        if (!string.Equals(catalogName, view.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist view '{catalogName}' because its catalog key and definition name differ.");
        }

        var statement = SqlParser.Parse(view.Sql, SqlParameterMap.Parse(view.Sql));
        if (statement is not CreateViewStatement persisted
            || !string.Equals(persisted.Name, view.Name, StringComparison.OrdinalIgnoreCase)
            || !SameColumnList(persisted.Columns, view.Columns))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist view '{catalogName}' because its SQL cannot reconstruct its catalog definition.");
        }

        ValidateRuntimeIndependentQuery("view", catalogName, view.Query);
        ValidateRuntimeIndependentQuery("view", catalogName, persisted.Query);
    }

    private static void ValidateTriggerDefinition(
        string catalogName,
        TriggerDefinition trigger,
        IReadOnlyDictionary<string, EmbeddedTable> tables)
    {
        if (!string.Equals(catalogName, trigger.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist trigger '{catalogName}' because its catalog key and definition name differ.");
        }
        if (!tables.ContainsKey(trigger.TableName))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist trigger '{catalogName}' because its table '{trigger.TableName}' does not exist.");
        }

        var statement = SqlParser.Parse(trigger.Sql, SqlParameterMap.Parse(trigger.Sql));
        if (statement is not CreateTriggerStatement persisted
            || !string.Equals(persisted.Name, trigger.Name, StringComparison.OrdinalIgnoreCase)
            || persisted.Event != trigger.Event
            || !string.Equals(persisted.TableName, trigger.TableName, StringComparison.OrdinalIgnoreCase)
            || persisted.Body.Count != trigger.Body.Count
            || !HaveSameStatementKinds(persisted.Body, trigger.Body))
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist trigger '{catalogName}' because its SQL cannot reconstruct its statement-level definition.");
        }

        ValidateRuntimeIndependentTriggerBody(catalogName, trigger.Body);
        ValidateRuntimeIndependentTriggerBody(catalogName, persisted.Body);
    }

    private static void ValidateStoredView(SchemaEntry entry, CreateViewStatement view)
    {
        if (!string.Equals(view.Name, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"Stored schema entry for view '{entry.Name}' does not match its CREATE VIEW name.");
        }

        ValidateRuntimeIndependentQuery("view", entry.Name, view.Query);
    }

    private static void ValidateStoredTrigger(
        SchemaEntry entry,
        CreateTriggerStatement trigger,
        IReadOnlyDictionary<string, EmbeddedTable> tables)
    {
        if (!string.Equals(trigger.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(trigger.TableName, entry.TableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddedSqlException(
                $"Stored schema entry for trigger '{entry.Name}' does not match its CREATE TRIGGER definition.");
        }
        if (!tables.ContainsKey(trigger.TableName))
        {
            throw new EmbeddedSqlException(
                $"Stored trigger '{entry.Name}' references missing table '{trigger.TableName}'.");
        }

        ValidateRuntimeIndependentTriggerBody(entry.Name, trigger.Body);
    }

    private static bool SameColumnList(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => left is null || right is null
            ? left is null && right is null
            : left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);

    private static bool HaveSameStatementKinds(
        IReadOnlyList<ParsedStatement> left,
        IReadOnlyList<ParsedStatement> right)
    {
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].GetType() != right[index].GetType())
                return false;
        }

        return true;
    }

    private static void ValidateRuntimeIndependentQuery(string objectType, string name, QueryStatement query)
    {
        var dependency = FindRuntimeDependency(query);
        if (dependency is not null)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist {objectType} '{name}' because it uses {dependency}. "
                + "File-backed schema definitions cannot retain bind parameters, managed callbacks, or custom collations across reopen.");
        }
    }

    private static void ValidateRuntimeIndependentTriggerBody(
        string name,
        IReadOnlyList<ParsedStatement> statements)
    {
        foreach (var statement in statements)
        {
            var dependency = FindRuntimeDependency(statement);
            if (dependency is not null)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist trigger '{name}' because it uses {dependency}. "
                    + "File-backed schema definitions cannot retain bind parameters, managed callbacks, or custom collations across reopen.");
            }
        }
    }

    private static string? FindRuntimeDependency(QueryStatement query)
    {
        return query switch
        {
            SelectStatement select => FirstRuntimeDependency(
                FindRuntimeDependency(select.Projections),
                FindRuntimeDependency(select.Source),
                FindRuntimeDependency(select.Where),
                FindRuntimeDependency(select.GroupBy),
                FindRuntimeDependency(select.Having),
                FindRuntimeDependency(select.OrderBy),
                FindRuntimeDependency(select.Limit),
                FindRuntimeDependency(select.Offset)),
            ValuesClause values => FindRuntimeDependency(values.Rows),
            CompoundSelectStatement compound => FirstRuntimeDependency(
                FindRuntimeDependency(compound.Terms),
                FindRuntimeDependency(compound.OrderBy),
                FindRuntimeDependency(compound.Limit),
                FindRuntimeDependency(compound.Offset)),
            WithSelectStatement with => FirstRuntimeDependency(
                FindRuntimeDependency(with.CommonTableExpressions),
                FindRuntimeDependency(with.Query)),
            _ => $"unsupported query type {query.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(TableSource? source)
    {
        return source switch
        {
            null => null,
            NamedTableSource => null,
            GenerateSeriesSource series => FirstRuntimeDependency(
                FindRuntimeDependency(series.Start),
                FindRuntimeDependency(series.Stop),
                FindRuntimeDependency(series.Step)),
            DerivedTableSource derived => FindRuntimeDependency(derived.Query),
            JoinTableSource join => FirstRuntimeDependency(
                FindRuntimeDependency(join.Left),
                FindRuntimeDependency(join.Right),
                FindRuntimeDependency(join.Condition)),
            _ => $"unsupported table source {source.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(ParsedStatement statement)
    {
        return statement switch
        {
            InsertStatement insert => FirstRuntimeDependency(
                FindRuntimeDependency(insert.Rows),
                FindRuntimeDependency(insert.Returning)),
            UpdateStatement update => FirstRuntimeDependency(
                FindRuntimeDependency(update.Assignments),
                FindRuntimeDependency(update.Where),
                FindRuntimeDependency(update.Returning)),
            DeleteStatement delete => FirstRuntimeDependency(
                FindRuntimeDependency(delete.Where),
                FindRuntimeDependency(delete.Returning)),
            _ => $"unsupported trigger body statement {statement.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(Expression? expression)
    {
        return expression switch
        {
            null or LiteralExpression or ColumnExpression or StarExpression or QualifiedStarExpression => null,
            ParameterExpression => "a bind parameter",
            FunctionExpression function => $"function {function.Name}()",
            ScalarSubqueryExpression subquery => FindRuntimeDependency(subquery.Query),
            ExistsExpression exists => FindRuntimeDependency(exists.Query),
            CollationExpression collation => $"explicit collation '{collation.Name}'",
            CastExpression cast => FindRuntimeDependency(cast.Expression),
            CaseExpression @case => FirstRuntimeDependency(
                FindRuntimeDependency(@case.Operand),
                FindRuntimeDependency(@case.Clauses),
                FindRuntimeDependency(@case.Else)),
            LikeExpression like => FirstRuntimeDependency(
                FindRuntimeDependency(like.Value),
                FindRuntimeDependency(like.Pattern),
                FindRuntimeDependency(like.Escape)),
            InExpression @in => FirstRuntimeDependency(
                FindRuntimeDependency(@in.Value),
                FindRuntimeDependency(@in.Values)),
            InSubqueryExpression @in => FirstRuntimeDependency(
                FindRuntimeDependency(@in.Value),
                FindRuntimeDependency(@in.Query)),
            BetweenExpression between => FirstRuntimeDependency(
                FindRuntimeDependency(between.Value),
                FindRuntimeDependency(between.Lower),
                FindRuntimeDependency(between.Upper)),
            UnaryExpression unary => FindRuntimeDependency(unary.Operand),
            GlobExpression glob => FirstRuntimeDependency(
                FindRuntimeDependency(glob.Value),
                FindRuntimeDependency(glob.Pattern)),
            BinaryExpression binary => FirstRuntimeDependency(
                FindRuntimeDependency(binary.Left),
                FindRuntimeDependency(binary.Right)),
            _ => $"unsupported expression {expression.GetType().Name}",
        };
    }

    private static string? FindRuntimeDependency(IEnumerable<Projection>? projections)
    {
        if (projections is null)
            return null;

        foreach (var projection in projections)
        {
            var dependency = FindRuntimeDependency(projection.Expression);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<OrderByTerm> terms)
    {
        foreach (var term in terms)
        {
            var dependency = FindRuntimeDependency(term.Expression);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<Expression> expressions)
    {
        foreach (var expression in expressions)
        {
            var dependency = FindRuntimeDependency(expression);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(
        IReadOnlyList<IReadOnlyList<Expression>> rows)
    {
        foreach (var row in rows)
        {
            var dependency = FindRuntimeDependency(row);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<QueryStatement> queries)
    {
        foreach (var query in queries)
        {
            var dependency = FindRuntimeDependency(query);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<CommonTableExpression> expressions)
    {
        foreach (var expression in expressions)
        {
            var dependency = FindRuntimeDependency(expression.Query);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<CaseClause> clauses)
    {
        foreach (var clause in clauses)
        {
            var dependency = FirstRuntimeDependency(
                FindRuntimeDependency(clause.When),
                FindRuntimeDependency(clause.Then));
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FindRuntimeDependency(IEnumerable<ColumnAssignment> assignments)
    {
        foreach (var assignment in assignments)
        {
            var dependency = FindRuntimeDependency(assignment.Value);
            if (dependency is not null)
                return dependency;
        }

        return null;
    }

    private static string? FirstRuntimeDependency(params string?[] dependencies)
        => dependencies.FirstOrDefault(static dependency => dependency is not null);

    private PreparedTableTree BuildWithoutRowidTableTree(
        string name,
        EmbeddedTable table,
        RebuildPageAllocator allocator)
    {
        var primaryKeySchema = ValidateWithoutRowidTableRepresentable(name, table);
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var records = BuildWithoutRowidTableRecords(name, table, primaryKeySchema, comparer);
        try
        {
            var treeDescription = $"WITHOUT ROWID table '{name}'";
            var indexTree = BuildIndexTreeFromLeafGroups(
                treeDescription,
                PartitionIndexLeafRecords(treeDescription, records, comparer),
                comparer,
                allocator);
            return new PreparedTableTree(
                indexTree.RootPage,
                indexTree.InteriorPages,
                indexTree.LeafPages,
                indexTree.OverflowPages);
        }
        catch (InvalidOperationException exception)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{name}' because its primary-key index cannot be represented as a valid SQLite index b-tree.",
                exception);
        }
    }

    private PreparedTableTree BuildTableTree(
        string name,
        EmbeddedTable table,
        RebuildPageAllocator allocator)
    {
        var leafGroups = PartitionTableLeafCells(name, table);
        var overflowPages = new List<PageImage>();
        if (leafGroups.Count == 1)
        {
            return new PreparedTableTree(
                BuildTableLeafPage(leafGroups[0], allocator, overflowPages),
                Array.Empty<PageImage>(),
                Array.Empty<PageImage>(),
                overflowPages);
        }

        var leafPageNumbers = new uint[leafGroups.Count];
        for (var leafIndex = 0; leafIndex < leafPageNumbers.Length; leafIndex++)
            leafPageNumbers[leafIndex] = allocator.ReservePage();

        var leafPages = new List<PageImage>(leafGroups.Count);
        for (var leafIndex = 0; leafIndex < leafGroups.Count; leafIndex++)
        {
            leafPages.Add(new PageImage(
                leafPageNumbers[leafIndex],
                BuildTableLeafPage(leafGroups[leafIndex], allocator, overflowPages)));
        }

        var leafChildren = leafGroups
            .Select((group, index) => new TableTreeChild(
                leafPageNumbers[index],
                group[^1].RowId))
            .ToArray();
        var interiorPages = new List<PageImage>();
        IReadOnlyList<TableTreeChild> levelChildren = leafChildren;
        byte[] root;
        while (!TryBuildTableInteriorPage(levelChildren, out root))
        {
            var groups = PartitionTableInteriorChildren(levelChildren);
            var parentChildren = new TableTreeChild[groups.Count];
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var pageNumber = allocator.ReservePage();
                interiorPages.Add(new PageImage(
                    pageNumber,
                    BuildTableInteriorPage(group)));
                parentChildren[groupIndex] = new TableTreeChild(pageNumber, group[^1].MaximumRowId);
            }

            levelChildren = parentChildren;
        }

        return new PreparedTableTree(root, interiorPages, leafPages, overflowPages);
    }

    private List<List<TableTreeChild>> PartitionTableInteriorChildren(
        IReadOnlyList<TableTreeChild> children)
    {
        if (children.Count < 2)
            throw new ArgumentException("A table-interior partition requires at least two children.", nameof(children));

        var groups = new List<List<TableTreeChild>> { new() };
        foreach (var child in children)
        {
            var currentGroup = groups[^1];
            currentGroup.Add(child);
            if (TryBuildTableInteriorPage(currentGroup, out _))
                continue;

            currentGroup.RemoveAt(currentGroup.Count - 1);
            if (currentGroup.Count == 0)
            {
                throw new InvalidOperationException(
                    "A SQLite table-interior page cannot contain one child.");
            }

            groups.Add(new List<TableTreeChild> { child });
        }

        if (groups.Count > 1 && groups[^1].Count == 1)
        {
            var previousGroup = groups[^2];
            var movedChild = previousGroup[^1];
            previousGroup.RemoveAt(previousGroup.Count - 1);
            groups[^1].Insert(0, movedChild);
            if (previousGroup.Count == 0
                || !TryBuildTableInteriorPage(previousGroup, out _)
                || !TryBuildTableInteriorPage(groups[^1], out _))
            {
                throw new InvalidOperationException(
                    "SQLite table-interior child partitioning cannot preserve non-empty child pages.");
            }
        }

        return groups;
    }

    private byte[] BuildTableInteriorPage(IReadOnlyList<TableTreeChild> children)
    {
        if (!TryBuildTableInteriorPage(children, out var page))
        {
            throw new InvalidOperationException(
                "SQLite table-interior cells and their pointer array do not fit in the page's usable space.");
        }

        return page;
    }

    private bool TryBuildTableInteriorPage(
        IReadOnlyList<TableTreeChild> children,
        out byte[] page)
        => TryBuildTableInteriorPage(children, isFirstPage: false, out page);

    private bool TryBuildTableInteriorPage(
        IReadOnlyList<TableTreeChild> children,
        bool isFirstPage,
        out byte[] page)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count == 0)
            throw new ArgumentException("A table-interior page requires at least one child.", nameof(children));

        try
        {
            var builder = new SqliteTableInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                children[^1].PageNumber,
                isFirstPage);
            for (var childIndex = 0; childIndex < children.Count - 1; childIndex++)
            {
                var child = children[childIndex];
                builder.Append(SqliteTableInteriorCell.Create(child.PageNumber, child.MaximumRowId));
            }

            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = [];
            return false;
        }
    }

    private List<List<PendingTableCell>> PartitionTableLeafCells(string name, EmbeddedTable table)
    {
        var leafGroups = new List<List<PendingTableCell>> { new() };
        var builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage: false);
        foreach (var (rowId, record) in EnumerateRowCells(name, table))
        {
            var pending = new PendingTableCell(
                rowId,
                record,
                CreateTableLeafPlanningCell(rowId, record));
            try
            {
                builder.Append(pending.PlanningCell);
            }
            catch (InvalidOperationException) when (leafGroups[^1].Count > 0)
            {
                leafGroups.Add([]);
                builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage: false);
                try
                {
                    builder.Append(pending.PlanningCell);
                }
                catch (InvalidOperationException exception)
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist table '{name}' because rowid {rowId} cannot fit in a SQLite table leaf.",
                        exception);
                }
            }
            catch (InvalidOperationException exception)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist table '{name}' because rowid {rowId} cannot fit in a SQLite table leaf.",
                    exception);
            }

            leafGroups[^1].Add(pending);
        }

        return leafGroups;
    }

    private SqliteTableLeafCell CreateTableLeafPlanningCell(long rowId, ReadOnlySpan<byte> record)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        return layout.UsesOverflow
            ? SqliteTableLeafCell.Create(
                rowId,
                checked((ulong)record.Length),
                record[..layout.LocalPayloadLength],
                firstOverflowPage: 1,
                _usableSpace)
            : SqliteTableLeafCell.Create(rowId, record, _usableSpace);
    }

    private byte[] BuildTableLeafPage(
        IReadOnlyList<PendingTableCell> cells,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage: false);
        foreach (var cell in cells)
            builder.Append(CreateTableLeafCell(cell.RowId, cell.Record, allocator, overflowPages));
        return builder.Build();
    }

    private PreparedIndexTree BuildIndexTree(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        RebuildPageAllocator allocator)
    {
        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var leafGroups = PartitionIndexLeafRecords(
            $"index '{index.Name}' on table '{tableName}'",
            BuildIndexRecords(tableName, table, index, comparer),
            comparer);
        return BuildIndexTreeFromLeafGroups(
            $"index '{index.Name}'",
            leafGroups,
            comparer,
            allocator);
    }

    private PreparedIndexTree BuildIndexTreeFromLeafGroups(
        string treeDescription,
        IReadOnlyList<List<byte[]>> leafGroups,
        SqliteIndexRecordComparer comparer,
        RebuildPageAllocator allocator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeDescription);
        ArgumentNullException.ThrowIfNull(leafGroups);
        var overflowPages = new List<PageImage>();
        if (leafGroups.Count == 1)
        {
            return new PreparedIndexTree(
                BuildIndexLeafPage(leafGroups[0], comparer, allocator, overflowPages),
                Array.Empty<PageImage>(),
                Array.Empty<PageImage>(),
                overflowPages);
        }

        var leafChildren = new IndexTreeNode[leafGroups.Count];
        for (var leafIndex = 0; leafIndex < leafGroups.Count; leafIndex++)
        {
            leafChildren[leafIndex] = IndexTreeNode.CreateLeaf(
                allocator.ReservePage(),
                new List<byte[]>(leafGroups[leafIndex]));
        }

        IReadOnlyList<IndexTreeNode> levelChildren = leafChildren;
        while (true)
        {
            var root = TryBuildIndexInteriorPlan(
                CloneIndexTreeNodes(levelChildren),
                treeDescription,
                comparer,
                throwOnPromotionFailure: false);
            if (root is not null)
            {
                var interiorPages = new List<PageImage>();
                var leafPages = new List<PageImage>();
                MaterializeIndexTreeChildren(
                    root.Children,
                    comparer,
                    allocator,
                    overflowPages,
                    interiorPages,
                    leafPages);
                return new PreparedIndexTree(
                    BuildIndexInteriorPage(root, comparer, allocator, overflowPages),
                    interiorPages,
                    leafPages,
                    overflowPages);
            }

            var plans = PartitionIndexInteriorChildren(treeDescription, levelChildren, comparer);
            var parentChildren = new IndexTreeNode[plans.Count];
            for (var planIndex = 0; planIndex < plans.Count; planIndex++)
            {
                parentChildren[planIndex] = IndexTreeNode.CreateInterior(
                    allocator.ReservePage(),
                    plans[planIndex]);
            }

            levelChildren = parentChildren;
        }
    }

    private IndexInteriorPlan? TryBuildIndexInteriorPlan(
        IReadOnlyList<IndexTreeNode> children,
        string indexName,
        SqliteIndexRecordComparer comparer,
        bool throwOnPromotionFailure)
    {
        if (children.Count < 2)
        {
            throw new ArgumentException(
                "A SQLite index-interior page requires at least two children.",
                nameof(children));
        }

        var childHeight = GetIndexTreeHeight(children[0]);
        for (var childIndex = 1; childIndex < children.Count; childIndex++)
        {
            if (GetIndexTreeHeight(children[childIndex]) != childHeight)
            {
                throw new InvalidOperationException(
                    "SQLite index-interior planning requires every child to have the same height.");
            }
        }

        var separators = new List<byte[]>(children.Count - 1);
        try
        {
            for (var childIndex = 0; childIndex < children.Count - 1; childIndex++)
            {
                separators.Add(PromoteIndexTreeSeparator(
                    children[childIndex],
                    children[childIndex + 1],
                    indexName));
            }
        }
        catch (EmbeddedSqlException) when (!throwOnPromotionFailure)
        {
            return null;
        }

        try
        {
            var builder = new SqliteIndexInteriorPageBuilder(
                _pageSize,
                _usableSpace,
                children[^1].PageNumber,
                comparer);
            for (var childIndex = 0; childIndex < separators.Count; childIndex++)
            {
                var separator = separators[childIndex];
                builder.Append(
                    SqliteIndexInteriorCell.Create(
                        children[childIndex].PageNumber,
                        CreateIndexLeafPlanningCell(separator)),
                    separator);
            }

            _ = builder.Build();
            return new IndexInteriorPlan(children, separators);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private IReadOnlyList<IndexInteriorPlan> PartitionIndexInteriorChildren(
        string treeDescription,
        IReadOnlyList<IndexTreeNode> children,
        SqliteIndexRecordComparer comparer)
    {
        if (children.Count < 2)
            throw new ArgumentException("An index-interior partition requires at least two children.", nameof(children));

        var groups = new List<IndexInteriorGroupRange>();
        var start = 0;
        while (start < children.Count)
        {
            if (children.Count - start == 1)
            {
                groups.Add(new IndexInteriorGroupRange(start, 1));
                break;
            }

            var bestCount = 0;
            for (var candidateCount = 2;
                 start + candidateCount <= children.Count;
                 candidateCount++)
            {
                var candidate = TryBuildIndexInteriorPlan(
                    CloneIndexTreeNodes(children, start, candidateCount),
                    treeDescription,
                    comparer,
                    throwOnPromotionFailure: false);
                if (candidate is null)
                    break;

                bestCount = candidateCount;
            }

            if (bestCount == 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist {treeDescription} because promoting its index separators would require rebalancing, which this storage layer does not implement.");
            }

            groups.Add(new IndexInteriorGroupRange(start, bestCount));
            start += bestCount;
        }

        if (groups[^1].Count == 1)
        {
            if (groups.Count < 2 || groups[^2].Count < 3)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist {treeDescription} because promoting its index separators would require rebalancing, which this storage layer does not implement.");
            }

            var previous = groups[^2];
            var last = groups[^1];
            groups[^2] = previous with { Count = previous.Count - 1 };
            groups[^1] = new IndexInteriorGroupRange(last.Start - 1, last.Count + 1);
        }

        var plans = new List<IndexInteriorPlan>(groups.Count);
        foreach (var group in groups)
        {
            var plan = TryBuildIndexInteriorPlan(
                CloneIndexTreeNodes(children, group.Start, group.Count),
                treeDescription,
                comparer,
                throwOnPromotionFailure: true);
            if (plan is null)
            {
                throw new InvalidOperationException(
                    "A planned SQLite index-interior page no longer fits its validated child partition.");
            }

            plans.Add(plan);
        }

        return plans;
    }

    private static byte[] PromoteIndexTreeSeparator(
        IndexTreeNode left,
        IndexTreeNode right,
        string treeDescription)
    {
        var leftLeaf = FindRightmostIndexLeaf(left);
        if (leftLeaf.Records.Count >= 2)
        {
            var separator = leftLeaf.Records[^1];
            leftLeaf.Records.RemoveAt(leftLeaf.Records.Count - 1);
            return separator;
        }

        var rightLeaf = FindLeftmostIndexLeaf(right);
        if (rightLeaf.Records.Count >= 2)
        {
            var separator = rightLeaf.Records[0];
            rightLeaf.Records.RemoveAt(0);
            return separator;
        }

        throw new EmbeddedSqlException(
            $"The managed file engine cannot persist {treeDescription} because promoting index separators would require rebalancing, which this storage layer does not implement.");
    }

    private byte[] BuildIndexInteriorPage(
        IndexInteriorPlan plan,
        SqliteIndexRecordComparer comparer,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        if (plan.Separators.Count != plan.Children.Count - 1)
            throw new InvalidOperationException("SQLite index-interior planning produced an invalid separator count.");

        var builder = new SqliteIndexInteriorPageBuilder(
            _pageSize,
            _usableSpace,
            plan.Children[^1].PageNumber,
            comparer);
        for (var childIndex = 0; childIndex < plan.Separators.Count; childIndex++)
        {
            var separator = plan.Separators[childIndex];
            var key = CreateIndexLeafCell(separator, allocator, overflowPages);
            builder.Append(
                SqliteIndexInteriorCell.Create(plan.Children[childIndex].PageNumber, key),
                separator);
        }

        return builder.Build();
    }

    private void MaterializeIndexTreeChildren(
        IReadOnlyList<IndexTreeNode> children,
        SqliteIndexRecordComparer comparer,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages,
        ICollection<PageImage> interiorPages,
        ICollection<PageImage> leafPages)
    {
        foreach (var child in children)
        {
            if (child.IsLeaf)
            {
                leafPages.Add(new PageImage(
                    child.PageNumber,
                    BuildIndexLeafPage(child.Records, comparer, allocator, overflowPages)));
                continue;
            }

            var plan = child.InteriorPlan
                ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
            MaterializeIndexTreeChildren(
                plan.Children,
                comparer,
                allocator,
                overflowPages,
                interiorPages,
                leafPages);
            interiorPages.Add(new PageImage(
                child.PageNumber,
                BuildIndexInteriorPage(plan, comparer, allocator, overflowPages)));
        }
    }

    private static IndexTreeNode FindLeftmostIndexLeaf(IndexTreeNode node)
    {
        while (!node.IsLeaf)
            node = (node.InteriorPlan
                ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan."))
                .Children[0];
        return node;
    }

    private static IndexTreeNode FindRightmostIndexLeaf(IndexTreeNode node)
    {
        while (!node.IsLeaf)
        {
            var children = (node.InteriorPlan
                ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan."))
                .Children;
            node = children[^1];
        }

        return node;
    }

    private static int GetIndexTreeHeight(IndexTreeNode node)
        => node.IsLeaf
            ? 0
            : checked(1 + GetIndexTreeHeight(
                (node.InteriorPlan
                    ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan."))
                .Children[0]));

    private static IndexTreeNode[] CloneIndexTreeNodes(
        IReadOnlyList<IndexTreeNode> nodes,
        int start,
        int count)
    {
        if (start < 0 || count < 0 || start > nodes.Count - count)
            throw new ArgumentOutOfRangeException(nameof(start), "SQLite index child range is outside the planned tree.");

        var clone = new IndexTreeNode[count];
        for (var index = 0; index < count; index++)
            clone[index] = CloneIndexTreeNode(nodes[start + index]);
        return clone;
    }

    private static IndexTreeNode[] CloneIndexTreeNodes(IReadOnlyList<IndexTreeNode> nodes)
        => CloneIndexTreeNodes(nodes, 0, nodes.Count);

    private static IndexTreeNode CloneIndexTreeNode(IndexTreeNode node)
    {
        if (node.IsLeaf)
            return IndexTreeNode.CreateLeaf(node.PageNumber, new List<byte[]>(node.Records));

        var plan = node.InteriorPlan
            ?? throw new InvalidOperationException("SQLite index tree node is missing its interior plan.");
        return IndexTreeNode.CreateInterior(
            node.PageNumber,
            new IndexInteriorPlan(
                CloneIndexTreeNodes(plan.Children),
                plan.Separators.Select(separator => separator.ToArray()).ToArray()));
    }

    private List<List<byte[]>> PartitionIndexLeafRecords(
        string treeDescription,
        IReadOnlyList<byte[]> records,
        SqliteIndexRecordComparer comparer)
    {
        var leafGroups = new List<List<byte[]>> { new() };
        var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
        foreach (var record in records)
        {
            var planningCell = CreateIndexLeafPlanningCell(record);
            try
            {
                builder.Append(planningCell, record);
            }
            catch (InvalidOperationException) when (leafGroups[^1].Count > 0)
            {
                leafGroups.Add([]);
                builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
                try
                {
                    builder.Append(planningCell, record);
                }
                catch (InvalidOperationException exception)
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist {treeDescription} because one key cannot fit in a SQLite index leaf.",
                        exception);
                }
            }
            catch (InvalidOperationException exception)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist {treeDescription} because one key cannot fit in a SQLite index leaf.",
                    exception);
            }

            leafGroups[^1].Add(record);
        }

        return leafGroups;
    }

    private SqliteIndexLeafCell CreateIndexLeafPlanningCell(ReadOnlySpan<byte> record)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        return layout.UsesOverflow
            ? SqliteIndexLeafCell.Create(
                checked((ulong)record.Length),
                record[..layout.LocalPayloadLength],
                firstOverflowPage: 1,
                _usableSpace)
            : SqliteIndexLeafCell.Create(record, _usableSpace);
    }

    private byte[] BuildIndexLeafPage(
        IReadOnlyList<byte[]> records,
        SqliteIndexRecordComparer comparer,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var builder = new SqliteIndexLeafPageBuilder(_pageSize, _usableSpace, comparer);
        foreach (var record in records)
            builder.Append(CreateIndexLeafCell(record, allocator, overflowPages), record);
        return builder.Build();
    }

    private SqliteTableLeafCell CreateTableLeafCell(
        long rowId,
        byte[] record,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.TableLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        if (!layout.UsesOverflow)
            return SqliteTableLeafCell.Create(rowId, record, _usableSpace);

        var overflowPayload = record.AsSpan(layout.LocalPayloadLength);
        var payloadCapacity = _usableSpace - SqliteOverflowPageView.HeaderLength;
        var overflowPageCount = checked((uint)((overflowPayload.Length + payloadCapacity - 1) / payloadCapacity));
        var overflowPageNumbers = new uint[checked((int)overflowPageCount)];
        for (var pageOffset = 0; pageOffset < overflowPageNumbers.Length; pageOffset++)
            overflowPageNumbers[pageOffset] = allocator.ReservePage();

        for (var pageOffset = 0U; pageOffset < overflowPageCount; pageOffset++)
        {
            var pageNumber = overflowPageNumbers[pageOffset];
            var payloadOffset = checked((int)(pageOffset * (uint)payloadCapacity));
            var payloadLength = Math.Min(payloadCapacity, overflowPayload.Length - payloadOffset);
            var nextOverflowPage = pageOffset + 1 == overflowPageCount
                ? 0
                : overflowPageNumbers[pageOffset + 1];
            overflowPages.Add(new PageImage(
                pageNumber,
                SqliteOverflowPageView.Create(
                    _pageSize,
                    _usableSpace,
                    nextOverflowPage,
                    overflowPayload.Slice(payloadOffset, payloadLength)).ToArray()));
        }

        return SqliteTableLeafCell.Create(
            rowId,
            checked((ulong)record.Length),
            record.AsSpan(0, layout.LocalPayloadLength),
            overflowPageNumbers[0],
            _usableSpace);
    }

    private SqliteIndexLeafCell CreateIndexLeafCell(
        byte[] record,
        RebuildPageAllocator allocator,
        ICollection<PageImage> overflowPages)
    {
        var layout = SqlitePayloadLayout.Calculate(
            SqliteBtreePageType.IndexLeaf,
            checked((ulong)record.Length),
            _usableSpace);
        if (!layout.UsesOverflow)
            return SqliteIndexLeafCell.Create(record, _usableSpace);

        var overflowPayload = record.AsSpan(layout.LocalPayloadLength);
        var payloadCapacity = _usableSpace - SqliteOverflowPageView.HeaderLength;
        var overflowPageCount = checked((uint)((overflowPayload.Length + payloadCapacity - 1) / payloadCapacity));
        var overflowPageNumbers = new uint[checked((int)overflowPageCount)];
        for (var pageOffset = 0; pageOffset < overflowPageNumbers.Length; pageOffset++)
            overflowPageNumbers[pageOffset] = allocator.ReservePage();

        for (var pageOffset = 0U; pageOffset < overflowPageCount; pageOffset++)
        {
            var pageNumber = overflowPageNumbers[pageOffset];
            var payloadOffset = checked((int)(pageOffset * (uint)payloadCapacity));
            var payloadLength = Math.Min(payloadCapacity, overflowPayload.Length - payloadOffset);
            var nextPageNumber = pageOffset + 1 == overflowPageCount
                ? 0
                : overflowPageNumbers[pageOffset + 1];
            overflowPages.Add(new PageImage(
                pageNumber,
                SqliteOverflowPageView.Create(
                    _pageSize,
                    _usableSpace,
                    nextPageNumber,
                    overflowPayload.Slice(payloadOffset, payloadLength)).ToArray()));
        }

        return SqliteIndexLeafCell.Create(
            checked((ulong)record.Length),
            record.AsSpan(0, layout.LocalPayloadLength),
            overflowPageNumbers[0],
            _usableSpace);
    }

    private IEnumerable<(long RowId, byte[] Record)> EnumerateRowCells(string name, EmbeddedTable table)
    {
        var aliasIndex = table.RowidAliasColumnIndex;

        // Pair every row with its tracked rowid and emit in ascending rowid order so the
        // leaf cells are sorted, as a valid SQLite b-tree requires. This preserves the
        // exact rowids across persistence for both alias and hidden-rowid tables.
        var ordered = table.Rows
            .Select((row, index) => (
                RowId: index < table.RowIds.Count ? table.RowIds[index] : index + 1,
                Row: row))
            .OrderBy(entry => entry.RowId);
        foreach (var (rowId, row) in ordered)
        {
            if (aliasIndex >= 0)
            {
                // A single-column INTEGER PRIMARY KEY is a rowid alias: store its value as
                // the SQLite rowid and NULL in the record, exactly as SQLite does.
                var record = (SqlValue[])row.Clone();
                record[aliasIndex] = SqlValue.Null;
                yield return (rowId, SqliteRecordCodec.Encode(record, _textEncoding));
            }
            else
            {
                yield return (rowId, SqliteRecordCodec.Encode(row, _textEncoding));
            }
        }
    }

    private IReadOnlyList<byte[]> BuildWithoutRowidTableRecords(
        string tableName,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        SqliteIndexRecordComparer comparer)
    {
        if (table.Rows.Count != table.RowIds.Count)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its row and rowid counts are inconsistent.");
        }

        table.ValidateRows(table.Rows);
        var records = new List<WithoutRowidRecord>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            if (row.Length != table.ColumnDefinitions.Length)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because a row has an invalid column count.");
            }

            var key = primaryKeySchema.ProjectKey(row);
            if (key.Any(value => value.Kind == SqlValueKind.Null))
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary key contains NULL.");
            }

            var record = SqliteRecordCodec.Encode(
                OrderWithoutRowidRecord(tableName, table, primaryKeySchema, row),
                _textEncoding);
            comparer.Validate(record);
            records.Add(new WithoutRowidRecord(record, key));
        }

        records.Sort((left, right) => comparer.Compare(left.Record, right.Record));
        for (var index = 1; index < records.Count; index++)
        {
            if (comparer.Compare(records[index - 1].Key, records[index].Key) >= 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary keys are not strictly increasing in BINARY order.");
            }
            if (comparer.Compare(records[index - 1].Record, records[index].Record) >= 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its complete SQLite index records are not strictly ordered.");
            }
        }

        return records.Select(record => record.Record).ToArray();
    }

    private static SqlValue[] OrderWithoutRowidRecord(
        string tableName,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        IReadOnlyList<SqlValue> row)
    {
        var values = new SqlValue[row.Count];
        var primaryKeyColumns = new bool[row.Count];
        var destination = 0;
        foreach (var term in primaryKeySchema.Terms)
        {
            if (term.ColumnIndex >= row.Count || primaryKeyColumns[term.ColumnIndex])
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist WITHOUT ROWID table '{tableName}' because its primary-key metadata is inconsistent.");
            }

            primaryKeyColumns[term.ColumnIndex] = true;
            values[destination++] = row[term.ColumnIndex];
        }

        for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
        {
            if (!primaryKeyColumns[columnIndex])
                values[destination++] = row[columnIndex];
        }

        if (destination != values.Length)
            throw new InvalidOperationException("WITHOUT ROWID record column ordering is incomplete.");

        return values;
    }

    private static SqlValue[] RestoreWithoutRowidRecord(
        string tableName,
        EmbeddedTable table,
        SqlitePrimaryKeySchema primaryKeySchema,
        IReadOnlyList<SqlValue> storedValues)
    {
        if (storedValues.Count != table.ColumnDefinitions.Length)
        {
            throw new InvalidDataException(
                $"Stored WITHOUT ROWID table '{tableName}' record has {storedValues.Count} column(s), but the schema declares {table.ColumnDefinitions.Length}.");
        }

        var row = new SqlValue[storedValues.Count];
        var primaryKeyColumns = new bool[row.Length];
        var source = 0;
        foreach (var term in primaryKeySchema.Terms)
        {
            if (term.ColumnIndex >= row.Length || primaryKeyColumns[term.ColumnIndex])
            {
                throw new InvalidDataException(
                    $"Stored WITHOUT ROWID table '{tableName}' has inconsistent primary-key metadata.");
            }

            primaryKeyColumns[term.ColumnIndex] = true;
            row[term.ColumnIndex] = storedValues[source++];
        }

        for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
        {
            if (!primaryKeyColumns[columnIndex])
                row[columnIndex] = storedValues[source++];
        }

        if (source != storedValues.Count)
            throw new InvalidDataException($"Stored WITHOUT ROWID table '{tableName}' record has trailing values.");

        return row;
    }

    private IReadOnlyList<byte[]> BuildIndexRecords(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index,
        SqliteIndexRecordComparer comparer)
    {
        if (table.Rows.Count != table.RowIds.Count)
        {
            throw new EmbeddedSqlException(
                $"The managed file engine cannot persist index '{index.Name}' because table '{tableName}' has inconsistent row and rowid counts.");
        }

        var records = new List<byte[]>(table.Rows.Count);
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            if (row.Length != table.ColumnDefinitions.Length)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because table '{tableName}' has a row with an invalid column count.");
            }

            var values = new SqlValue[index.Columns.Count + 1];
            for (var column = 0; column < index.Columns.Count; column++)
                values[column] = row[index.Columns[column].ColumnIndex];
            values[^1] = SqlValue.Integer(table.RowIds[rowIndex]);
            var record = SqliteRecordCodec.Encode(values, _textEncoding);
            comparer.Validate(record);
            records.Add(record);
        }

        records.Sort((left, right) => comparer.Compare(left, right));
        for (var indexPosition = 1; indexPosition < records.Count; indexPosition++)
        {
            if (comparer.Compare(records[indexPosition - 1], records[indexPosition]) >= 0)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because its complete SQLite index keys are not strictly ordered.");
            }
        }

        return records;
    }

    private PreparedSchemaTree BuildSchemaTree(
        IReadOnlyList<SchemaEntry> entries,
        RebuildPageAllocator allocator)
    {
        var cells = new List<SqliteTableLeafCell>(entries.Count);
        long rowId = 1;
        foreach (var entry in entries)
        {
            var record = SqliteRecordCodec.Encode(
                [
                    SqlValue.Text(entry.Type),
                    SqlValue.Text(entry.Name),
                    SqlValue.Text(entry.TableName),
                    SqlValue.Integer(entry.RootPage),
                    SqlValue.Text(entry.Sql),
                ],
                _textEncoding);
            if (SqlitePayloadLayout.Calculate(
                    SqliteBtreePageType.TableLeaf,
                    checked((ulong)record.Length),
                    _usableSpace).UsesOverflow)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist the schema for '{entry.Name}' because schema overflow pages are not supported.");
            }

            try
            {
                cells.Add(SqliteTableLeafCell.Create(rowId++, record, _usableSpace));
            }
            catch (ArgumentException exception)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist the schema for '{entry.Name}' because its row cannot fit in a SQLite schema page.",
                    exception);
            }
        }

        if (TryBuildSchemaLeafPage(cells, isFirstPage: true, out var rootPage))
            return new PreparedSchemaTree(rootPage, Array.Empty<PageImage>(), Array.Empty<PageImage>());

        var leafGroups = PartitionSchemaLeafCells(cells);
        if (leafGroups.Count == 1)
            leafGroups = SplitSchemaLeafGroup(leafGroups[0]);

        var leafPages = new List<PageImage>(leafGroups.Count);
        var leafChildren = new TableTreeChild[leafGroups.Count];
        for (var leafIndex = 0; leafIndex < leafGroups.Count; leafIndex++)
        {
            var pageNumber = allocator.ReservePage();
            leafPages.Add(new PageImage(
                pageNumber,
                BuildSchemaLeafPage(leafGroups[leafIndex], isFirstPage: false)));
            leafChildren[leafIndex] = new TableTreeChild(
                pageNumber,
                leafGroups[leafIndex][^1].RowId);
        }

        IReadOnlyList<TableTreeChild> levelChildren = leafChildren;
        var interiorPages = new List<PageImage>();
        while (!TryBuildTableInteriorPage(levelChildren, isFirstPage: true, out rootPage))
        {
            var groups = PartitionTableInteriorChildren(levelChildren);
            var parentChildren = new TableTreeChild[groups.Count];
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var pageNumber = allocator.ReservePage();
                interiorPages.Add(new PageImage(
                    pageNumber,
                    BuildTableInteriorPage(group)));
                parentChildren[groupIndex] = new TableTreeChild(pageNumber, group[^1].MaximumRowId);
            }

            levelChildren = parentChildren;
        }

        return new PreparedSchemaTree(rootPage, interiorPages, leafPages);
    }

    private List<List<SqliteTableLeafCell>> PartitionSchemaLeafCells(
        IReadOnlyList<SqliteTableLeafCell> cells)
    {
        var groups = new List<List<SqliteTableLeafCell>> { new() };
        foreach (var cell in cells)
        {
            var group = groups[^1];
            group.Add(cell);
            if (TryBuildSchemaLeafPage(group, isFirstPage: false, out _))
                continue;

            group.RemoveAt(group.Count - 1);
            if (group.Count == 0)
            {
                throw new EmbeddedSqlException(
                    "The managed file engine cannot persist a sqlite_schema row because it does not fit in a SQLite schema page.");
            }

            groups.Add([cell]);
            if (!TryBuildSchemaLeafPage(groups[^1], isFirstPage: false, out _))
            {
                throw new EmbeddedSqlException(
                    "The managed file engine cannot persist a sqlite_schema row because it does not fit in a SQLite schema page.");
            }
        }

        return groups;
    }

    private List<List<SqliteTableLeafCell>> SplitSchemaLeafGroup(
        IReadOnlyList<SqliteTableLeafCell> cells)
    {
        for (var leftCount = 1; leftCount < cells.Count; leftCount++)
        {
            var left = cells.Take(leftCount).ToList();
            var right = cells.Skip(leftCount).ToList();
            if (TryBuildSchemaLeafPage(left, isFirstPage: false, out _)
                && TryBuildSchemaLeafPage(right, isFirstPage: false, out _))
            {
                return [left, right];
            }
        }

        throw new EmbeddedSqlException(
            "The managed file engine cannot promote the sqlite_schema root because a single schema row requires a non-root page.");
    }

    private byte[] BuildSchemaLeafPage(
        IReadOnlyList<SqliteTableLeafCell> cells,
        bool isFirstPage)
    {
        if (!TryBuildSchemaLeafPage(cells, isFirstPage, out var page))
        {
            throw new InvalidOperationException(
                "SQLite schema table-leaf cells and their pointer array do not fit in the page's usable space.");
        }

        return page;
    }

    private bool TryBuildSchemaLeafPage(
        IReadOnlyList<SqliteTableLeafCell> cells,
        bool isFirstPage,
        out byte[] page)
    {
        try
        {
            var builder = new SqliteTableLeafPageBuilder(_pageSize, _usableSpace, isFirstPage);
            foreach (var cell in cells)
                builder.Append(cell);
            page = builder.Build();
            return true;
        }
        catch (InvalidOperationException)
        {
            page = null!;
            return false;
        }
    }

    private static List<SchemaEntry> BuildSchemaEntries(
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers,
        IReadOnlyDictionary<string, uint> rootPages,
        IReadOnlyDictionary<string, uint> indexRootPages)
    {
        var entries = new List<SchemaEntry>();
        foreach (var name in tables.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new SchemaEntry(
                "table",
                name,
                name,
                rootPages[name],
                EmbeddedDatabase.BuildCreateTableSql(name, tables[name])));
        }

        foreach (var tableName in tables.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var index in tables[tableName].Indexes.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!indexRootPages.TryGetValue(index.Name, out var rootPage))
                {
                    throw new InvalidOperationException(
                        $"SQLite schema construction is missing root page for index '{index.Name}'.");
                }

                entries.Add(new SchemaEntry(
                    "index",
                    index.Name,
                    tableName,
                    rootPage,
                    BuildCreateIndexSql(tableName, index)));
            }
        }

        foreach (var name in views.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var view = views[name];
            entries.Add(new SchemaEntry("view", view.Name, view.Name, 0, view.Sql));
        }

        foreach (var name in triggers.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var trigger = triggers[name];
            entries.Add(new SchemaEntry("trigger", trigger.Name, trigger.TableName, 0, trigger.Sql));
        }

        return entries;
    }

    private static IReadOnlyList<IndexDefinition> GetIndexDefinitions(
        IReadOnlyList<string> tableNames,
        IReadOnlyDictionary<string, EmbeddedTable> tables,
        IReadOnlyDictionary<string, ViewDefinition> views,
        IReadOnlyDictionary<string, TriggerDefinition> triggers)
    {
        var names = new HashSet<string>(tables.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var name in views.Keys)
        {
            if (!names.Add(name))
                throw new EmbeddedSqlException($"The managed file engine cannot persist duplicate schema name '{name}'.");
        }
        foreach (var name in triggers.Keys)
        {
            if (!names.Add(name))
                throw new EmbeddedSqlException($"The managed file engine cannot persist duplicate schema name '{name}'.");
        }

        var definitions = new List<IndexDefinition>();
        foreach (var tableName in tableNames)
        {
            var table = tables[tableName];
            foreach (var index in table.Indexes.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                ValidateIndexRepresentable(tableName, table, index);
                if (!names.Add(index.Name))
                {
                    throw new EmbeddedSqlException(
                        $"The managed file engine cannot persist index '{index.Name}' because its schema name is already in use.");
                }

                definitions.Add(new IndexDefinition(tableName, table, index));
            }
        }

        return definitions;
    }

    private static void ValidateIndexRepresentable(
        string tableName,
        EmbeddedTable table,
        EmbeddedIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (string.IsNullOrWhiteSpace(index.Name))
            throw new EmbeddedSqlException($"The managed file engine cannot persist an unnamed index on table '{tableName}'.");
        if (index.Columns.Count == 0)
            throw new EmbeddedSqlException($"The managed file engine cannot persist index '{index.Name}' because it has no key columns.");

        foreach (var column in index.Columns)
        {
            if (column.ColumnIndex < 0 || column.ColumnIndex >= table.Columns.Length)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because it has an invalid column reference.");
            }
            if (!string.Equals(table.Columns[column.ColumnIndex], column.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because its column metadata is inconsistent.");
            }
            if (column.Descending)
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because descending index terms are not yet supported for file-backed databases.");
            }
            if (column.Collation is not null
                && !string.Equals(column.Collation, "BINARY", StringComparison.OrdinalIgnoreCase))
            {
                throw new EmbeddedSqlException(
                    $"The managed file engine cannot persist index '{index.Name}' because collation '{column.Collation}' is not BINARY.");
            }
        }
    }

    private static EmbeddedIndex CreateIndexDefinition(
        string tableName,
        EmbeddedTable table,
        CreateIndexStatement statement)
    {
        if (statement.Columns.Count == 0)
            throw new EmbeddedSqlException($"Stored index '{statement.Name}' has no key columns.");

        var columns = new EmbeddedIndexColumn[statement.Columns.Count];
        for (var index = 0; index < statement.Columns.Count; index++)
        {
            var column = statement.Columns[index];
            var columnIndex = Array.FindIndex(
                table.Columns,
                name => string.Equals(name, column.Name, StringComparison.OrdinalIgnoreCase));
            if (columnIndex < 0)
            {
                throw new EmbeddedSqlException(
                    $"Stored index '{statement.Name}' references missing column '{column.Name}' on table '{tableName}'.");
            }

            columns[index] = new EmbeddedIndexColumn(
                table.Columns[columnIndex],
                columnIndex,
                column.Collation,
                column.Descending);
        }

        return new EmbeddedIndex(statement.Name, statement.Unique, columns);
    }

    private void ValidateStoredIndex(
        SchemaEntry entry,
        EmbeddedTable table,
        EmbeddedIndex index,
        ISet<uint> occupiedBtreePages)
    {
        if (entry.RootPage < 2 || entry.RootPage > _pager.CommittedPageCount)
        {
            throw new EmbeddedSqlException(
                $"Stored index '{entry.Name}' has invalid rootpage {entry.RootPage}.");
        }

        var overflowReader = new SqliteOverflowChainReader(_pager, _header);
        IReadOnlyList<byte[]> actualRecords;
        try
        {
            var rootPage = _pager.ReadCommittedPage(entry.RootPage);
            var rootHeader = SqliteBtreePageHeader.Parse(rootPage);
            actualRecords = rootHeader.PageType switch
            {
                SqliteBtreePageType.IndexLeaf => ReadIndexLeafRecords(rootPage, overflowReader),
                SqliteBtreePageType.IndexInterior => ReadIndexInteriorRecords(
                    entry,
                    rootPage,
                    overflowReader,
                    occupiedBtreePages),
                _ => throw new InvalidDataException(
                    $"SQLite index root page has unsupported page type {rootHeader.PageType}."),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
        {
            throw new EmbeddedSqlException(
                $"Stored index '{entry.Name}' is not a valid supported SQLite index b-tree.",
                exception);
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var expectedRecords = BuildIndexRecords(entry.TableName, table, index, comparer);
        if (actualRecords.Count != expectedRecords.Count)
        {
            throw new EmbeddedSqlException(
                $"Stored index '{entry.Name}' has {actualRecords.Count} record(s), but table '{entry.TableName}' requires {expectedRecords.Count}.");
        }

        for (var recordIndex = 0; recordIndex < expectedRecords.Count; recordIndex++)
        {
            if (!actualRecords[recordIndex].AsSpan().SequenceEqual(expectedRecords[recordIndex]))
            {
                throw new EmbeddedSqlException(
                    $"Stored index '{entry.Name}' does not match table '{entry.TableName}' at record {recordIndex}.");
            }
        }
    }

    private IReadOnlyList<byte[]> ReadIndexLeafRecords(
        ReadOnlySpan<byte> page,
        SqliteOverflowChainReader overflowReader)
    {
        var leaf = SqliteIndexLeafPageView.Parse(
            page,
            _usableSpace,
            _textEncoding,
            overflowReader: overflowReader);
        var records = new byte[leaf.Cells.Count][];
        for (var index = 0; index < records.Length; index++)
            records[index] = leaf.GetRecord(index);
        return records;
    }

    private IReadOnlyList<byte[]> ReadIndexInteriorRecords(
        SchemaEntry entry,
        ReadOnlySpan<byte> rootPage,
        SqliteOverflowChainReader overflowReader,
        ISet<uint> occupiedBtreePages)
    {
        return ReadIndexInteriorNodeRecords(
            entry,
            entry.RootPage,
            rootPage,
            overflowReader,
            occupiedBtreePages).Records;
    }

    private IndexTreeReadResult ReadIndexInteriorNodeRecords(
        SchemaEntry entry,
        uint pageNumber,
        ReadOnlySpan<byte> pageImage,
        SqliteOverflowChainReader overflowReader,
        ISet<uint> occupiedBtreePages)
    {
        var interior = SqliteIndexInteriorPageView.Parse(
            pageImage,
            _usableSpace,
            _textEncoding,
            overflowReader: overflowReader);
        if (interior.Cells.Count == 0)
        {
            throw new InvalidDataException(
                $"Stored index '{entry.Name}' has an unsupported index-interior page {pageNumber} without a separator.");
        }

        SqliteBtreePageType? directChildType = null;
        foreach (var childPage in interior.Cells
                     .Select(cell => cell.Cell.LeftChildPage)
                     .Append(interior.Header.RightMostChildPage))
        {
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references invalid child page {childPage}.");
            }

            var currentChildType = SqliteBtreePageHeader.Parse(_pager.ReadCommittedPage(childPage)).PageType;
            if (currentChildType is not (SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.IndexInterior))
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references unsupported child type {currentChildType}.");
            }
            if (directChildType is { } expectedChildType && currentChildType != expectedChildType)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
            }

            directChildType = currentChildType;
        }

        var comparer = new SqliteIndexRecordComparer(_textEncoding);
        var records = new List<byte[]>();
        byte[]? previousRecord = null;
        int? childHeight = null;
        SqliteBtreePageType? childType = null;
        for (var childIndex = 0; childIndex <= interior.Cells.Count; childIndex++)
        {
            var childPage = childIndex == interior.Cells.Count
                ? interior.Header.RightMostChildPage
                : interior.Cells[childIndex].Cell.LeftChildPage;
            if (childPage < 2 || childPage > _pager.CommittedPageCount)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references invalid child page {childPage}.");
            }
            if (!occupiedBtreePages.Add(childPage))
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} reuses b-tree page {childPage} as a child.");
            }

            var childPageImage = _pager.ReadCommittedPage(childPage);
            var childHeader = SqliteBtreePageHeader.Parse(childPageImage);
            if (childHeader.PageType is not (SqliteBtreePageType.IndexLeaf or SqliteBtreePageType.IndexInterior))
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} references unsupported child type {childHeader.PageType}.");
            }
            if (childType is { } expectedChildType && childHeader.PageType != expectedChildType)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
            }

            childType = childHeader.PageType;
            IndexTreeReadResult childResult;
            switch (childHeader.PageType)
            {
                case SqliteBtreePageType.IndexLeaf:
                    {
                        var leafRecords = ReadIndexLeafRecords(childPageImage, overflowReader);
                        if (leafRecords.Count == 0)
                        {
                            throw new InvalidDataException(
                                $"Stored index '{entry.Name}' has an empty leaf child page {childPage}.");
                        }

                        childResult = new IndexTreeReadResult(leafRecords, 0);
                        break;
                    }
                case SqliteBtreePageType.IndexInterior:
                    childResult = ReadIndexInteriorNodeRecords(
                        entry,
                        childPage,
                        childPageImage,
                        overflowReader,
                        occupiedBtreePages);
                    break;
                default:
                    throw new InvalidOperationException("SQLite index child type validation is incomplete.");
            }

            if (childHeight is { } expectedHeight && childResult.Height != expectedHeight)
            {
                throw new InvalidDataException(
                    $"Stored index '{entry.Name}' interior page {pageNumber} mixes index-leaf and index-interior non-leaf children.");
            }

            childHeight = childResult.Height;
            AppendIndexRecords(
                entry.Name,
                records,
                childResult.Records,
                comparer,
                ref previousRecord,
                $"interior page {pageNumber} children");
            if (childIndex < interior.Cells.Count)
            {
                var separator = interior.GetRecord(childIndex);
                if (comparer.Compare(childResult.Records[^1], separator) >= 0)
                {
                    throw new InvalidDataException(
                        $"Stored index '{entry.Name}' interior page {pageNumber} separator {childIndex} does not follow child page {childPage}.");
                }

                AppendIndexRecord(
                    entry.Name,
                    records,
                    separator,
                    comparer,
                    ref previousRecord,
                    $"interior page {pageNumber} children");
            }
        }

        return new IndexTreeReadResult(
            records,
            checked((childHeight ?? throw new InvalidDataException(
                $"Stored index '{entry.Name}' has an empty interior page {pageNumber}.")) + 1));
    }

    private static void AppendIndexRecords(
        string indexName,
        ICollection<byte[]> records,
        IReadOnlyList<byte[]> values,
        SqliteIndexRecordComparer comparer,
        ref byte[]? previousRecord,
        string level)
    {
        foreach (var value in values)
            AppendIndexRecord(indexName, records, value, comparer, ref previousRecord, level);
    }

    private static void AppendIndexRecord(
        string indexName,
        ICollection<byte[]> records,
        byte[] value,
        SqliteIndexRecordComparer comparer,
        ref byte[]? previousRecord,
        string level)
    {
        if (previousRecord is not null && comparer.Compare(previousRecord, value) >= 0)
        {
            throw new InvalidDataException(
                $"Stored index '{indexName}' {level} are not globally ordered by their complete BINARY keys.");
        }

        records.Add(value);
        previousRecord = value;
    }

    private void ValidateSchemaEntries(IReadOnlyList<SchemaEntry> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootPages = new HashSet<uint>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || !names.Add(entry.Name))
                throw new EmbeddedSqlException("Managed file database sqlite_schema has duplicate or empty object names.");

            switch (entry.Type)
            {
                case "table":
                    if (!string.Equals(entry.Name, entry.TableName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database table '{entry.Name}' has a mismatched sqlite_schema table name.");
                    }
                    goto case "index";
                case "index":
                    if (entry.RootPage < 2 || entry.RootPage > _pager.CommittedPageCount)
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database {entry.Type} '{entry.Name}' has invalid rootpage {entry.RootPage}.");
                    }
                    if (!rootPages.Add(entry.RootPage))
                    {
                        throw new EmbeddedSqlException(
                            $"Managed file database sqlite_schema reuses rootpage {entry.RootPage}.");
                    }
                    break;
                case "view":
                    if (entry.RootPage != 0 || !string.Equals(entry.Name, entry.TableName, StringComparison.OrdinalIgnoreCase))
                        throw new EmbeddedSqlException($"Managed file database view '{entry.Name}' has an invalid sqlite_schema rootpage or table name.");
                    break;
                case "trigger":
                    if (entry.RootPage != 0)
                        throw new EmbeddedSqlException($"Managed file database trigger '{entry.Name}' has a non-zero rootpage.");
                    break;
                default:
                    throw new EmbeddedSqlException(
                        $"Managed file database has unsupported sqlite_schema type '{entry.Type}'.");
            }
        }
    }

    private static string BuildCreateIndexSql(string tableName, EmbeddedIndex index)
    {
        var columns = index.Columns.Select(column =>
        {
            var definition = QuoteIdentifier(column.Name);
            if (column.Collation is { } collation)
                definition += " COLLATE " + collation;
            if (column.Descending)
                definition += " DESC";
            return definition;
        });
        var unique = index.Unique ? "UNIQUE " : string.Empty;
        return $"CREATE {unique}INDEX {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(tableName)} ({string.Join(", ", columns)})";
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string ComputeSchemaSignature(IReadOnlyList<SchemaEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(entry.Type).Append('\u0001')
                .Append(entry.Name).Append('\u0001')
                .Append(entry.TableName).Append('\u0001')
                .Append(entry.RootPage).Append('\u0001')
                .Append(entry.Sql).Append('\u0002');
        }

        return builder.ToString();
    }

    private static string RequireText(SqlValue value, string field)
        => value.Kind == SqlValueKind.Text
            ? value.AsText()
            : throw new EmbeddedSqlException($"Managed file database sqlite_schema column '{field}' is not text.");

    private static long RequireInteger(SqlValue value, string field)
        => value.Kind == SqlValueKind.Integer
            ? value.AsInteger()
            : throw new EmbeddedSqlException($"Managed file database sqlite_schema column '{field}' is not an integer.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Assigns page numbers for one complete catalog replacement.
    /// </summary>
    /// <remarks>
    /// A regular replacement consumes validated former freelist leaves first,
    /// then may reinitialize other old pages because the replacement transaction
    /// proves its entire active/freelist partition before its WAL commit marker.
    /// It is not an in-place page allocator.
    /// </remarks>
    private sealed class RebuildPageAllocator
    {
        private readonly uint _sourcePageCount;
        private readonly bool _compact;
        private readonly Queue<uint> _reusableLeaves;
        private readonly HashSet<uint> _reusableLeafSet;
        private uint _nextExistingPage;
        private uint _nextAppendedPage;

        public RebuildPageAllocator(
            uint sourcePageCount,
            IReadOnlyList<uint> reusableLeaves,
            bool compact)
        {
            ArgumentOutOfRangeException.ThrowIfZero(sourcePageCount);
            ArgumentNullException.ThrowIfNull(reusableLeaves);

            _sourcePageCount = sourcePageCount;
            _compact = compact;
            _reusableLeafSet = new HashSet<uint>();
            _reusableLeaves = new Queue<uint>();
            if (!compact)
            {
                foreach (var pageNumber in reusableLeaves.Order())
                {
                    if (pageNumber < 2 || pageNumber > sourcePageCount || !_reusableLeafSet.Add(pageNumber))
                    {
                        throw new InvalidDataException(
                            "Managed file rebuild received an invalid or duplicate validated freelist leaf.");
                    }

                    _reusableLeaves.Enqueue(pageNumber);
                }
            }

            _nextExistingPage = compact ? 0U : 2U;
            _nextAppendedPage = compact ? 2U : sourcePageCount == uint.MaxValue ? 0U : sourcePageCount + 1;
            HighestAllocatedPage = SchemaRootPage;
        }

        public uint HighestAllocatedPage { get; private set; }

        public uint ReservePage()
        {
            if (_reusableLeaves.TryDequeue(out var reusablePage))
                return RecordAllocation(reusablePage);

            while (!_compact && _nextExistingPage != 0)
            {
                var existingPage = _nextExistingPage;
                _nextExistingPage = existingPage == _sourcePageCount ? 0 : existingPage + 1;
                if (!_reusableLeafSet.Contains(existingPage))
                    return RecordAllocation(existingPage);
            }

            if (_nextAppendedPage == 0 || _nextAppendedPage == uint.MaxValue)
            {
                throw new EmbeddedSqlException(
                    "The managed file engine cannot allocate SQLite page UInt32.MaxValue.");
            }

            var appendedPage = _nextAppendedPage;
            _nextAppendedPage++;
            return RecordAllocation(appendedPage);
        }

        private uint RecordAllocation(uint pageNumber)
        {
            if (pageNumber < 2)
                throw new InvalidOperationException("Managed file rebuild cannot allocate SQLite page 1 as data.");
            if (pageNumber > HighestAllocatedPage)
                HighestAllocatedPage = pageNumber;
            return pageNumber;
        }
    }

    private sealed record SchemaEntry(string Type, string Name, string TableName, uint RootPage, string Sql);

    private sealed record PageImage(uint PageNumber, byte[] Page);

    private sealed record PendingTableCell(long RowId, byte[] Record, SqliteTableLeafCell PlanningCell);

    private sealed record WithoutRowidRecord(byte[] Record, SqlValue[] Key);

    private sealed record PreparedSchemaTree(
        byte[] RootPage,
        IReadOnlyList<PageImage> InteriorPages,
        IReadOnlyList<PageImage> LeafPages);

    private sealed record PreparedTableTree(
        byte[] RootPage,
        IReadOnlyList<PageImage> InteriorPages,
        IReadOnlyList<PageImage> LeafPages,
        IReadOnlyList<PageImage> OverflowPages);

    private sealed record TableTreeChild(uint PageNumber, long MaximumRowId);

    private readonly record struct SchemaTreeReadResult(long? MaximumRowId, int Height);

    private readonly record struct TableTreeReadResult(long MaximumRowId, int Height);

    private sealed record PreparedIndexTree(
        byte[] RootPage,
        IReadOnlyList<PageImage> InteriorPages,
        IReadOnlyList<PageImage> LeafPages,
        IReadOnlyList<PageImage> OverflowPages);

    private sealed record IndexTreeReadResult(IReadOnlyList<byte[]> Records, int Height);

    private sealed record IndexInteriorPlan(
        IReadOnlyList<IndexTreeNode> Children,
        IReadOnlyList<byte[]> Separators);

    private sealed class IndexTreeNode
    {
        private IndexTreeNode(uint pageNumber, List<byte[]>? records, IndexInteriorPlan? interiorPlan)
        {
            if (pageNumber == 0)
                throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");

            PageNumber = pageNumber;
            _records = records;
            InteriorPlan = interiorPlan;
        }

        private readonly List<byte[]>? _records;

        public uint PageNumber { get; }

        public bool IsLeaf => _records is not null;

        public List<byte[]> Records => _records
            ?? throw new InvalidOperationException("SQLite index interior nodes do not own leaf records.");

        public IndexInteriorPlan? InteriorPlan { get; }

        public static IndexTreeNode CreateLeaf(uint pageNumber, List<byte[]> records)
        {
            ArgumentNullException.ThrowIfNull(records);
            if (records.Count == 0)
                throw new ArgumentException("SQLite index leaf nodes must contain at least one record.", nameof(records));
            return new IndexTreeNode(pageNumber, records, interiorPlan: null);
        }

        public static IndexTreeNode CreateInterior(uint pageNumber, IndexInteriorPlan interiorPlan)
        {
            ArgumentNullException.ThrowIfNull(interiorPlan);
            if (interiorPlan.Children.Count < 2
                || interiorPlan.Separators.Count != interiorPlan.Children.Count - 1)
            {
                throw new ArgumentException("SQLite index interior nodes require a valid multi-child plan.", nameof(interiorPlan));
            }

            return new IndexTreeNode(pageNumber, records: null, interiorPlan);
        }
    }

    private sealed record IndexInteriorGroupRange(int Start, int Count);

    private sealed record IndexDefinition(string TableName, EmbeddedTable Table, EmbeddedIndex Index);
}
