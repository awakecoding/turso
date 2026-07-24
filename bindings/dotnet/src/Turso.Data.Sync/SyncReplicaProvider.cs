using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Turso.Data.Sync;

public static class ReplicaProviderRegistration
{
    public static void Register()
    {
        TursoReplicaProvider.Register(new SyncReplicaProviderFactory());
    }
}

internal sealed class SyncReplicaProviderFactory : TursoReplicaProviderFactory
{
    public override TursoReplicaDatabase OpenReplica(TursoReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SyncReplicaDatabase.Open(options);
    }

    public override async Task<TursoReplicaDatabase> OpenReplicaAsync(
        TursoReplicaOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await SyncReplicaDatabase.OpenAsync(options, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class SyncReplicaDatabase : TursoReplicaDatabase
{
    internal const string ClientName = "turso-dotnet";
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly HashSet<SyncNativeStatement> _statements = [];
    private readonly SyncDatabaseHandle _database;
    private SyncConnectionHandle? _connection;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly TursoReplicaOptions _options;
    private readonly Uri _remoteUri;
    private readonly string? _authToken;
    private readonly AsyncLocal<ApplicationCallbackScope?> _progressCallbackScope = new();
    private bool _disposeRequested;
    private bool _disposed;
    private int _operationThreadId;

    private SyncReplicaDatabase(
        SyncDatabaseHandle database,
        TursoReplicaOptions options,
        HttpMessageHandler? httpMessageHandler = null)
    {
        _database = database;
        _options = options;
        _remoteUri = options.RemoteUri;
        _authToken = options.AuthToken;
        _requestTimeout = options.HttpPolicy.RequestTimeout;
        var handler = httpMessageHandler ?? options.HttpPolicy.MessageHandler;
        _httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(
                handler,
                disposeHandler: httpMessageHandler is not null);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    internal static SyncReplicaDatabase Open(
        TursoReplicaOptions options,
        HttpMessageHandler httpMessageHandler)
    {
        ArgumentNullException.ThrowIfNull(httpMessageHandler);
        var database = CreateDatabase(options);
        SyncReplicaDatabase? replica = null;
        try
        {
            replica = new SyncReplicaDatabase(database, options, httpMessageHandler);
            replica.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return replica;
        }
        catch
        {
            replica?.Dispose();
            if (replica is null)
                database.Dispose();
            throw;
        }
    }

    public override bool IsInvalid => _disposed || _connection is null || _connection.IsInvalid;

    public static SyncReplicaDatabase Open(TursoReplicaOptions options)
    {
        var database = CreateDatabase(options);
        SyncReplicaDatabase? replica = null;
        try
        {
            replica = new SyncReplicaDatabase(database, options);
            replica.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return replica;
        }
        catch
        {
            replica?.Dispose();
            if (replica is null)
                database.Dispose();
            throw;
        }
    }

    public static async Task<SyncReplicaDatabase> OpenAsync(
        TursoReplicaOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = CreateDatabase(options);
        SyncReplicaDatabase? replica = null;
        try
        {
            replica = new SyncReplicaDatabase(database, options);
            await replica.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return replica;
        }
        catch
        {
            replica?.Dispose();
            if (replica is null)
                database.Dispose();
            throw;
        }
    }

    public override TursoNativeStatement PrepareStatement(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return UseExclusiveOperation(() =>
        {
            var connection = _connection ?? throw new InvalidOperationException("The embedded replica SQL connection is unavailable.");
            var status = SyncInterop.ConnectionPrepareSingle(connection, sql, out var statement, out var error);
            SyncNative.ThrowIfFailure(status, error);
            var nativeStatement = new SyncNativeStatement(SyncStatementHandle.FromRaw(statement), this);
            _statements.Add(nativeStatement);
            return nativeStatement;
        });
    }

    public override void SetBusyTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        UseExclusiveOperation(() =>
        {
            var connection = _connection
                ?? throw new InvalidOperationException("The embedded replica SQL connection is unavailable.");
            SyncInterop.ConnectionSetBusyTimeout(connection, checked((long)timeout.TotalMilliseconds));
        });
    }

    internal void Interrupt()
    {
        try
        {
            lock (_lifecycleLock)
            {
                if (_disposed || _connection is null)
                    return;
                SyncInterop.ConnectionInterrupt(_connection);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override async Task SyncAsync(CancellationToken cancellationToken)
    {
        _ = await SyncAsync(new TursoSyncOptions(), cancellationToken).ConfigureAwait(false);
    }

    public override async Task<TursoSyncResult> SyncAsync(
        TursoSyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfReentrantApplicationCode();
        ThrowIfUnavailable();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        await _operationGate.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        var progressScope = options.Progress is null ? null : EnterProgressCallbackScope();
        try
        {
            ThrowIfUnavailable();
            ReportProgress(options.Progress, TursoSyncProgressStage.Pushing);
            await PushChangesAsync(operationCancellation.Token).ConfigureAwait(false);
            ReportProgress(options.Progress, TursoSyncProgressStage.Pulling);
            var changesApplied = await PullChangesAsync(options.Progress, operationCancellation.Token).ConfigureAwait(false);
            var statistics = await GetStatisticsAsync(operationCancellation.Token).ConfigureAwait(false);
            var result = SyncNative.CreateResult(changesApplied, statistics);
            ReportProgress(options.Progress, TursoSyncProgressStage.Completed);
            return result;
        }
        finally
        {
            progressScope?.Dispose();
            _operationGate.Release();
        }
    }

    public override void Dispose()
    {
        EnsureCanClose();
        lock (_lifecycleLock)
        {
            if (_disposed || _disposeRequested)
                return;

            _disposeRequested = true;
        }

        _disposeCancellation.Cancel();
        _operationGate.Wait();
        try
        {
            foreach (var statement in _statements)
                statement.DisposeFromDatabase();
            _statements.Clear();
            _connection?.Dispose();
            _connection = null;
            _database.Dispose();
            _httpClient.Dispose();
            _disposeCancellation.Dispose();
            lock (_lifecycleLock)
            {
                _disposed = true;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal override void EnsureCanClose()
    {
        if (_progressCallbackScope.Value?.IsActive == true)
        {
            throw new InvalidOperationException(
                "An embedded replica cannot be closed from a sync progress callback.");
        }
        _options.ThrowIfApplicationHttpReentrant(closing: true);
    }

    internal T UseExclusiveOperation<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnterOperation();
        try
        {
            return operation();
        }
        finally
        {
            ExitOperation();
        }
    }

    internal void UseExclusiveOperation(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        UseExclusiveOperation(() =>
        {
            operation();
            return true;
        });
    }

    internal void DisposeStatement(SyncNativeStatement statement, SyncStatementHandle handle)
    {
        try
        {
            UseExclusiveOperation(() =>
            {
                if (_statements.Remove(statement))
                    handle.Dispose();
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void RunStatementIoWhileExclusive()
    {
        _ = DrainIoQueueAsync(CancellationToken.None, cancellationObserved: false)
            .GetAwaiter()
            .GetResult();
    }

    private static SyncDatabaseHandle CreateDatabase(TursoReplicaOptions options)
    {
        using var configuration = SyncReplicaConfiguration.Create(options);
        var databaseConfig = configuration.DatabaseConfig;
        var replicaConfig = configuration.ReplicaConfig;
        var status = SyncInterop.DatabaseNew(ref databaseConfig, ref replicaConfig, out var database, out var error);
        SyncNative.ThrowIfFailure(status, error);
        return SyncDatabaseHandle.FromRaw(database);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var operation = StartOperation(SyncInterop.DatabaseCreate);
        var result = await DriveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        if (result != SyncOperationResultType.None)
            throw new TursoException($"Unexpected result type {result} while creating an embedded replica.");

        using var connectOperation = StartOperation(SyncInterop.DatabaseConnect);
        result = await DriveOperationAsync(connectOperation, cancellationToken).ConfigureAwait(false);
        if (result != SyncOperationResultType.Connection)
            throw new TursoException($"Unexpected result type {result} while connecting an embedded replica.");

        var extractStatus = SyncInterop.OperationExtractConnection(connectOperation, out var connection);
        SyncNative.ThrowIfFailure(extractStatus, IntPtr.Zero);
        if (connection == IntPtr.Zero)
            throw new TursoException("The native sync SDK returned an empty SQL connection handle.");
        _connection = SyncConnectionHandle.FromRaw(connection);
    }

    private async Task PushChangesAsync(CancellationToken cancellationToken)
    {
        using var operation = StartOperation(SyncInterop.DatabasePushChanges);
        var result = await DriveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        if (result != SyncOperationResultType.None)
            throw new TursoException($"Unexpected result type {result} while pushing embedded replica changes.");
    }

    private async Task<bool> PullChangesAsync(
        IProgress<TursoSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var waitOperation = StartOperation(SyncInterop.DatabaseWaitChanges);
        var result = await DriveOperationAsync(waitOperation, cancellationToken).ConfigureAwait(false);
        if (result != SyncOperationResultType.Changes)
            throw new TursoException($"Unexpected result type {result} while pulling embedded replica changes.");

        var extractStatus = SyncInterop.OperationExtractChanges(waitOperation, out var changes);
        SyncNative.ThrowIfFailure(extractStatus, IntPtr.Zero);
        if (changes == IntPtr.Zero)
            return false;

        using var changesHandle = SyncChangesHandle.FromRaw(changes);
        ReportProgress(progress, TursoSyncProgressStage.Applying);
        var consumedChanges = changesHandle.Consume();
        using var applyOperation = StartOperation((SyncDatabaseHandle database, out IntPtr operation, out IntPtr error) =>
            SyncInterop.DatabaseApplyChanges(database, consumedChanges, out operation, out error));
        result = await DriveOperationAsync(applyOperation, cancellationToken).ConfigureAwait(false);
        if (result != SyncOperationResultType.None)
            throw new TursoException($"Unexpected result type {result} while applying embedded replica changes.");
        return true;
    }

    private async Task<TursoSyncStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        using var operation = StartOperation(SyncInterop.DatabaseStats);
        var result = await DriveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        if (result != SyncOperationResultType.Stats)
            throw new TursoException($"Unexpected result type {result} while reading embedded replica statistics.");

        var extractStatus = SyncInterop.OperationExtractStats(operation, out var statistics);
        SyncNative.ThrowIfFailure(extractStatus, IntPtr.Zero);
        return SyncNative.CopyStatistics(statistics);
    }

    private SyncOperationHandle StartOperation(SyncOperationStarter starter)
    {
        var status = starter(_database, out var operation, out var error);
        SyncNative.ThrowIfFailure(status, error);
        if (operation == IntPtr.Zero)
            throw new TursoException("The native sync SDK returned an empty operation handle.");
        return SyncOperationHandle.FromRaw(operation);
    }

    private async Task<SyncOperationResultType> DriveOperationAsync(
        SyncOperationHandle operation,
        CancellationToken cancellationToken)
    {
        var cancellationObserved = false;
        while (true)
        {
            cancellationObserved |= cancellationToken.IsCancellationRequested;
            var status = SyncInterop.OperationResume(operation, out var error);
            if (status == SyncStatusCode.Done)
            {
                SyncNative.ReleaseError(error);
                cancellationToken.ThrowIfCancellationRequested();
                return SyncInterop.OperationResultKind(operation);
            }

            if (status == SyncStatusCode.Io)
            {
                SyncNative.ReleaseError(error);
                cancellationObserved = await DrainIoQueueAsync(cancellationToken, cancellationObserved).ConfigureAwait(false);
                continue;
            }

            if (status == SyncStatusCode.Ok)
            {
                SyncNative.ReleaseError(error);
                continue;
            }

            var failure = SyncNative.CreateException(status, error);
            if (cancellationObserved || cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Embedded replica synchronization was canceled.", failure, cancellationToken);
            throw failure;
        }
    }

    private async Task<bool> DrainIoQueueAsync(CancellationToken cancellationToken, bool cancellationObserved)
    {
        while (true)
        {
            cancellationObserved |= cancellationToken.IsCancellationRequested;
            var status = SyncInterop.DatabaseTakeIoItem(_database, out var item, out var error);
            SyncNative.ThrowIfFailure(status, error);
            if (item == IntPtr.Zero)
                break;

            using var itemHandle = SyncIoItemHandle.FromRaw(item);
            cancellationObserved |= await HandleIoItemAsync(itemHandle, cancellationToken, cancellationObserved)
                .ConfigureAwait(false);
        }

        var callbackStatus = SyncInterop.DatabaseStepIoCallbacks(_database, out var callbackError);
        SyncNative.ThrowIfFailure(callbackStatus, callbackError);
        return cancellationObserved;
    }

    private async Task<bool> HandleIoItemAsync(
        SyncIoItemHandle item,
        CancellationToken cancellationToken,
        bool cancellationObserved)
    {
        if (cancellationObserved || cancellationToken.IsCancellationRequested)
        {
            CompleteIoFailure(item, "The embedded replica synchronization operation was canceled.");
            return true;
        }

        return SyncInterop.IoRequestKind(item) switch
        {
            SyncIoRequestType.Http => await HandleHttpRequestAsync(item, cancellationToken).ConfigureAwait(false),
            SyncIoRequestType.FullRead => await HandleFullReadRequestAsync(item, cancellationToken).ConfigureAwait(false),
            SyncIoRequestType.FullWrite => await HandleFullWriteRequestAsync(item, cancellationToken).ConfigureAwait(false),
            SyncIoRequestType.None => CompleteEmptyIoRequest(item),
            _ => throw new TursoException("The native sync SDK returned an unknown I/O request type."),
        };
    }

    private async Task<bool> HandleHttpRequestAsync(SyncIoItemHandle item, CancellationToken cancellationToken)
    {
        var requestStatus = SyncInterop.IoRequestHttp(item, out var nativeRequest);
        SyncNative.ThrowIfFailure(requestStatus, IntPtr.Zero);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_requestTimeout != Timeout.InfiniteTimeSpan)
            requestCancellation.CancelAfter(_requestTimeout);
        var requestToken = requestCancellation.Token;
        using var httpScope = _options.EnterApplicationHttpScope();

        try
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(SyncNative.CopyString(nativeRequest.Method)),
                BuildRequestUri(SyncNative.CopyString(nativeRequest.Url), SyncNative.CopyString(nativeRequest.Path)));
            var body = SyncNative.CopyBytes(nativeRequest.Body);
            if (body.Length > 0)
                request.Content = new ByteArrayContent(body);

            for (var index = 0; index < nativeRequest.Headers; index++)
            {
                var headerStatus = SyncInterop.IoRequestHttpHeader(item, checked((nuint)index), out var nativeHeader);
                SyncNative.ThrowIfFailure(headerStatus, IntPtr.Zero);
                AddRequestHeader(request, SyncNative.CopyString(nativeHeader.Key), SyncNative.CopyString(nativeHeader.Value));
            }

            if (!string.IsNullOrWhiteSpace(_authToken) && !request.Headers.Contains("Authorization"))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_authToken}");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken).ConfigureAwait(false);
            SyncNative.ThrowIfFailure(SyncInterop.IoStatus(item, (int)response.StatusCode), IntPtr.Zero);

            await using var responseStream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await responseStream.ReadAsync(buffer, requestToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                PushIoBuffer(item, buffer.AsSpan(0, read));
            }

            SyncNative.ThrowIfFailure(SyncInterop.IoDone(item), IntPtr.Zero);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteIoFailure(item, "The embedded replica synchronization operation was canceled.");
            return true;
        }
        catch (OperationCanceledException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (HttpRequestException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (IOException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (InvalidOperationException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (UriFormatException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (ArgumentException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (NotSupportedException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
    }

    private async Task<bool> HandleFullReadRequestAsync(SyncIoItemHandle item, CancellationToken cancellationToken)
    {
        var requestStatus = SyncInterop.IoRequestFullRead(item, out var nativeRequest);
        SyncNative.ThrowIfFailure(requestStatus, IntPtr.Zero);
        var path = SyncNative.CopyString(nativeRequest.Path);

        try
        {
            var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            PushIoBuffer(item, content);
            SyncNative.ThrowIfFailure(SyncInterop.IoDone(item), IntPtr.Zero);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteIoFailure(item, "The embedded replica synchronization operation was canceled.");
            return true;
        }
        catch (FileNotFoundException)
        {
            SyncNative.ThrowIfFailure(SyncInterop.IoDone(item), IntPtr.Zero);
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            SyncNative.ThrowIfFailure(SyncInterop.IoDone(item), IntPtr.Zero);
            return false;
        }
        catch (IOException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (ArgumentException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (NotSupportedException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
    }

    private async Task<bool> HandleFullWriteRequestAsync(SyncIoItemHandle item, CancellationToken cancellationToken)
    {
        var requestStatus = SyncInterop.IoRequestFullWrite(item, out var nativeRequest);
        SyncNative.ThrowIfFailure(requestStatus, IntPtr.Zero);
        var path = SyncNative.CopyString(nativeRequest.Path);
        var content = SyncNative.CopyBytes(nativeRequest.Content);

        try
        {
            await WriteFileAtomicallyAsync(path, content, cancellationToken).ConfigureAwait(false);
            SyncNative.ThrowIfFailure(SyncInterop.IoDone(item), IntPtr.Zero);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteIoFailure(item, "The embedded replica synchronization operation was canceled.");
            return true;
        }
        catch (IOException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (ArgumentException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
        catch (NotSupportedException exception)
        {
            CompleteIoFailure(item, exception.Message);
            return false;
        }
    }

    private static bool CompleteEmptyIoRequest(SyncIoItemHandle item)
    {
        SyncNative.ThrowIfFailure(SyncInterop.IoDone(item), IntPtr.Zero);
        return false;
    }

    private void CompleteIoFailure(SyncIoItemHandle item, string message)
    {
        var errorBytes = Encoding.UTF8.GetBytes(message);
        unsafe
        {
            fixed (byte* errorPointer = errorBytes)
            {
                var error = new SyncSlice
                {
                    Pointer = errorBytes.Length == 0 ? IntPtr.Zero : (IntPtr)errorPointer,
                    Length = checked((nuint)errorBytes.Length),
                };
                SyncNative.ThrowIfFailure(SyncInterop.IoPoison(item, ref error), IntPtr.Zero);
            }
        }

        SyncNative.ThrowIfFailure(SyncInterop.IoDone(item), IntPtr.Zero);
    }

    private static void PushIoBuffer(SyncIoItemHandle item, ReadOnlySpan<byte> buffer)
    {
        unsafe
        {
            fixed (byte* bufferPointer = buffer)
            {
                var nativeBuffer = new SyncSlice
                {
                    Pointer = buffer.IsEmpty ? IntPtr.Zero : (IntPtr)bufferPointer,
                    Length = checked((nuint)buffer.Length),
                };
                SyncNative.ThrowIfFailure(SyncInterop.IoPushBuffer(item, ref nativeBuffer), IntPtr.Zero);
            }
        }
    }

    private Uri BuildRequestUri(string configuredUrl, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absoluteUri))
            return absoluteUri;

        var baseUri = string.IsNullOrWhiteSpace(configuredUrl)
            ? _remoteUri
            : Uri.TryCreate(configuredUrl, UriKind.Absolute, out var requestUri)
                ? requestUri
                : throw new UriFormatException("The native sync SDK returned an invalid remote URL.");
        return new Uri(baseUri, path);
    }

    private static void AddRequestHeader(HttpRequestMessage request, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (request.Headers.TryAddWithoutValidation(name, value))
            return;

        request.Content ??= new ByteArrayContent([]);
        if (!request.Content.Headers.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException($"The native sync SDK returned an invalid HTTP header: {name}.");
    }

    internal static async Task WriteFileAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var moved = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void EnterOperation()
    {
        ThrowIfReentrantApplicationCode();
        if (Volatile.Read(ref _operationThreadId) == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "The native Sync connection does not support reentrant operations from callbacks.");
        }

        ThrowIfUnavailable();
        _operationGate.Wait();
        try
        {
            ThrowIfUnavailable();
            Volatile.Write(ref _operationThreadId, Environment.CurrentManagedThreadId);
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private void ExitOperation()
    {
        Volatile.Write(ref _operationThreadId, 0);
        _operationGate.Release();
    }

    private void ThrowIfUnavailable()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed || _disposeRequested, this);
        }
    }

    private void ReportProgress(
        IProgress<TursoSyncProgress>? progress,
        TursoSyncProgressStage stage)
    {
        if (progress is null)
            return;

        progress.Report(new TursoSyncProgress(stage));
    }

    private void ThrowIfReentrantApplicationCode()
    {
        if (_progressCallbackScope.Value?.IsActive == true)
        {
            throw new InvalidOperationException(
                "Embedded replica operations cannot be reentered from a sync progress callback.");
        }
        _options.ThrowIfApplicationHttpReentrant(closing: false);
    }

    private IDisposable EnterProgressCallbackScope()
    {
        var previousScope = _progressCallbackScope.Value;
        var scope = new ApplicationCallbackScope();
        _progressCallbackScope.Value = scope;
        return new ApplicationCallbackScopeLease(_progressCallbackScope, scope, previousScope);
    }

    private delegate SyncStatusCode SyncOperationStarter(
        SyncDatabaseHandle database,
        out IntPtr operation,
        out IntPtr error);

    private sealed class ApplicationCallbackScope
    {
        private int _isActive = 1;

        public bool IsActive => Volatile.Read(ref _isActive) != 0;

        public void Deactivate() => Interlocked.Exchange(ref _isActive, 0);
    }

    private sealed class ApplicationCallbackScopeLease(
        AsyncLocal<ApplicationCallbackScope?> currentScope,
        ApplicationCallbackScope scope,
        ApplicationCallbackScope? previousScope) : IDisposable
    {
        public void Dispose()
        {
            scope.Deactivate();
            currentScope.Value = previousScope;
        }
    }
}

internal sealed class SyncNativeStatement : TursoNativeStatement
{
    private readonly SyncStatementHandle _statement;
    private readonly SyncReplicaDatabase _database;

    public SyncNativeStatement(SyncStatementHandle statement, SyncReplicaDatabase database)
    {
        _statement = statement;
        _database = database;
    }

    public override bool IsInvalid => _statement.IsInvalid;

    public override int ParameterCount
        => _database.UseExclusiveOperation(() => checked((int)SyncInterop.StatementParameterCount(_statement)));

    public override void BindParameter(int index, TursoValue value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        _database.UseExclusiveOperation(() => Bind(checked((nuint)index), value));
    }

    public override int BindNamedParameter(string name, TursoValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _database.UseExclusiveOperation(() =>
        {
            var index = SyncInterop.StatementNamedPosition(_statement, name);
            if (index < 1)
                return 0;
            if (index > int.MaxValue)
                throw new InvalidOperationException($"Parameter index {index} is too large.");

            Bind(checked((nuint)index), value);
            return (int)index;
        });
    }

    public override string? GetParameterName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);
        return _database.UseExclusiveOperation(() =>
        {
            var pointer = SyncInterop.StatementParameterName(_statement, index);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUTF8(pointer);
            }
            finally
            {
                SyncInterop.StringDeinit(pointer);
            }
        });
    }

    public override bool Read()
    {
        return _database.UseExclusiveOperation(() =>
        {
            while (true)
            {
                var status = SyncInterop.StatementStep(_statement, out var error);
                if (status == SyncStatusCode.Row)
                {
                    SyncNative.ReleaseError(error);
                    return true;
                }
                if (status == SyncStatusCode.Done)
                {
                    SyncNative.ReleaseError(error);
                    return false;
                }
                if (status != SyncStatusCode.Io)
                    throw SyncNative.CreateException(status, error);

                SyncNative.ReleaseError(error);
                _database.RunStatementIoWhileExclusive();
                var ioStatus = SyncInterop.StatementRunIo(_statement, out var ioError);
                SyncNative.ThrowIfFailure(ioStatus, ioError);
            }
        });
    }

    public override void Interrupt() => _database.Interrupt();

    public override TursoValue GetValue(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        var index = checked((nuint)ordinal);
        return _database.UseExclusiveOperation(() => SyncInterop.StatementRowValueKind(_statement, index) switch
        {
            SyncValueType.Unknown => TursoValue.Empty(),
            SyncValueType.Null => TursoValue.Null(),
            SyncValueType.Integer => TursoValue.Int(SyncInterop.StatementRowValueInt(_statement, index)),
            SyncValueType.Real => TursoValue.Real(SyncInterop.StatementRowValueDouble(_statement, index)),
            SyncValueType.Text => TursoValue.String(Encoding.UTF8.GetString(ReadBytes(index))),
            SyncValueType.Blob => TursoValue.Blob(ReadBytes(index)),
            _ => throw new TursoException("The native sync SDK returned an unknown SQL value type."),
        });
    }

    public override string GetName(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return _database.UseExclusiveOperation(() =>
        {
            var pointer = SyncInterop.StatementColumnName(_statement, checked((nuint)ordinal));
            if (pointer == IntPtr.Zero)
                return string.Empty;

            try
            {
                return Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
            }
            finally
            {
                SyncInterop.StringDeinit(pointer);
            }
        });
    }

    public override int FieldCount
        => _database.UseExclusiveOperation(() => checked((int)SyncInterop.StatementColumnCount(_statement)));

    public override int RowsAffected
        => _database.UseExclusiveOperation(() => checked((int)SyncInterop.StatementRowsAffected(_statement)));

    public override bool HasRows
        => _database.UseExclusiveOperation(
            () => SyncInterop.StatementColumnCount(_statement) > 0
                && SyncInterop.StatementRowValueKind(_statement, 0) != SyncValueType.Unknown);

    public override void Dispose() => _database.DisposeStatement(this, _statement);

    internal void DisposeFromDatabase() => _statement.Dispose();

    private void Bind(nuint index, TursoValue value)
    {
        var status = value.ValueType switch
        {
            TursoValueType.Empty or TursoValueType.Null => SyncInterop.StatementBindNull(_statement, index),
            TursoValueType.Integer => SyncInterop.StatementBindInt(_statement, index, value.IntValue),
            TursoValueType.Real => SyncInterop.StatementBindDouble(_statement, index, value.RealValue),
            TursoValueType.Text => BindText(index, value.StringValue),
            TursoValueType.Blob => BindBlob(index, value.BlobValue),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
        SyncNative.ThrowIfFailure(status, IntPtr.Zero);
    }

    private SyncStatusCode BindText(nuint index, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        return BindBytes(bytes, pointer => SyncInterop.StatementBindText(_statement, index, pointer, checked((nuint)bytes.Length)));
    }

    private SyncStatusCode BindBlob(nuint index, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return BindBytes(value, pointer => SyncInterop.StatementBindBlob(_statement, index, pointer, checked((nuint)value.Length)));
    }

    private static SyncStatusCode BindBytes(byte[] value, Func<IntPtr, SyncStatusCode> bind)
    {
        if (value.Length == 0)
            return bind(IntPtr.Zero);

        unsafe
        {
            fixed (byte* pointer = value)
                return bind((IntPtr)pointer);
        }
    }

    private byte[] ReadBytes(nuint index)
    {
        var length = SyncInterop.StatementRowValueBytesCount(_statement, index);
        if (length < 0 || length > int.MaxValue)
            throw new TursoException("Unable to read native SQL value bytes.");
        if (length == 0)
            return [];

        var pointer = SyncInterop.StatementRowValueBytesPtr(_statement, index);
        if (pointer == IntPtr.Zero)
            throw new TursoException("Unable to read native SQL value bytes.");

        unsafe
        {
            return new ReadOnlySpan<byte>((void*)pointer, (int)length).ToArray();
        }
    }
}

internal static class SyncNative
{
    public static void ThrowIfFailure(SyncStatusCode status, IntPtr error)
    {
        if (status is SyncStatusCode.Ok or SyncStatusCode.Done or SyncStatusCode.Row)
        {
            ReleaseError(error);
            return;
        }

        throw CreateException(status, error);
    }

    public static TursoException CreateException(SyncStatusCode status, IntPtr error)
    {
        var message = ConsumeError(error);
        return new TursoException(message ?? $"Turso sync native call failed with status {status}.");
    }

    public static void ReleaseError(IntPtr error)
    {
        if (error != IntPtr.Zero)
            SyncInterop.StringDeinit(error);
    }

    public static string CopyString(SyncSlice slice)
        => Encoding.UTF8.GetString(CopyBytes(slice));

    public static unsafe byte[] CopyBytes(SyncSlice slice)
    {
        if (slice.Length == 0)
            return [];
        if (slice.Pointer == IntPtr.Zero || slice.Length > int.MaxValue)
            throw new TursoException("The native sync SDK returned an invalid byte slice.");

        return new ReadOnlySpan<byte>((void*)slice.Pointer, (int)slice.Length).ToArray();
    }

    public static TursoSyncStatistics CopyStatistics(SyncStats statistics)
    {
        return new TursoSyncStatistics(
            statistics.CdcOperations,
            statistics.MainWalSize,
            statistics.RevertWalSize,
            UnixTimeOrNull(statistics.LastPullUnixTime),
            UnixTimeOrNull(statistics.LastPushUnixTime),
            statistics.NetworkSentBytes,
            statistics.NetworkReceivedBytes,
            statistics.Revision.Pointer == IntPtr.Zero ? null : CopyString(statistics.Revision));
    }

    public static TursoSyncResult CreateResult(bool changesApplied, TursoSyncStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        return new TursoSyncResult(
            changesApplied ? TursoSyncOutcome.RemoteChangesApplied : TursoSyncOutcome.UpToDate,
            statistics);
    }

    private static DateTimeOffset? UnixTimeOrNull(long value)
        => value == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(value);

    private static string? ConsumeError(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(error);
        }
        finally
        {
            SyncInterop.StringDeinit(error);
        }
    }
}

internal sealed class NativeUtf8String : IDisposable
{
    private NativeUtf8String(IntPtr pointer)
    {
        Pointer = pointer;
    }

    public IntPtr Pointer { get; private set; }

    public static NativeUtf8String From(string? value)
        => new(value is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value));

    public void Dispose()
    {
        if (Pointer == IntPtr.Zero)
            return;

        Marshal.FreeCoTaskMem(Pointer);
        Pointer = IntPtr.Zero;
    }
}

internal sealed class SyncReplicaConfiguration : IDisposable
{
    private readonly NativeUtf8String _path;
    private readonly NativeUtf8String _remoteUrl;
    private readonly NativeUtf8String _clientName;
    private readonly NativeUtf8String _partialBootstrapQuery;
    private readonly NativeUtf8String _remoteEncryptionKey;
    private readonly NativeUtf8String _remoteEncryptionCipher;

    private SyncReplicaConfiguration(TursoReplicaOptions options)
    {
        options.Validate();
        _path = NativeUtf8String.From(options.Path);
        _remoteUrl = NativeUtf8String.From(options.RemoteUri.AbsoluteUri);
        _clientName = NativeUtf8String.From(SyncReplicaDatabase.ClientName);
        _partialBootstrapQuery = NativeUtf8String.From(options.PartialBootstrap?.Query);
        _remoteEncryptionKey = NativeUtf8String.From(options.RemoteEncryption?.Base64Key);
        _remoteEncryptionCipher = NativeUtf8String.From(options.RemoteEncryption?.NativeName);

        DatabaseConfig = new SyncDatabaseConfig
        {
            AsyncIo = 1,
            Path = _path.Pointer,
        };
        ReplicaConfig = new SyncReplicaConfig
        {
            Path = _path.Pointer,
            RemoteUrl = _remoteUrl.Pointer,
            ClientName = _clientName.Pointer,
            LongPollTimeoutMilliseconds = options.LongPollTimeout is { } longPollTimeout
                ? checked((int)longPollTimeout.TotalMilliseconds)
                : 0,
            BootstrapIfEmpty = options.BootstrapIfEmpty,
            ReservedBytes = options.RemoteEncryption?.ReservedBytes ?? 0,
            PartialBootstrapStrategyPrefix = options.PartialBootstrap?.PrefixLength ?? 0,
            PartialBootstrapStrategyQuery = _partialBootstrapQuery.Pointer,
            PartialBootstrapSegmentSize = ToNativeSize(options.PartialBootstrap?.SegmentSize),
            PartialBootstrapPrefetch = options.PartialBootstrap?.Prefetch ?? false,
            RemoteEncryptionKey = _remoteEncryptionKey.Pointer,
            RemoteEncryptionCipher = _remoteEncryptionCipher.Pointer,
            PushOperationsThreshold = ToNativeSize(options.PushOperationsThreshold),
            PullBytesThreshold = ToNativeSize(options.PullBytesThreshold),
            LogicalMvccPull = false,
        };
    }

    public SyncDatabaseConfig DatabaseConfig { get; }

    public SyncReplicaConfig ReplicaConfig { get; }

    public static SyncReplicaConfiguration Create(TursoReplicaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SyncReplicaConfiguration(options);
    }

    public void Dispose()
    {
        _remoteEncryptionCipher.Dispose();
        _remoteEncryptionKey.Dispose();
        _partialBootstrapQuery.Dispose();
        _clientName.Dispose();
        _remoteUrl.Dispose();
        _path.Dispose();
    }

    private static nuint ToNativeSize(long? value)
        => value is null ? 0 : checked((nuint)value.Value);
}
