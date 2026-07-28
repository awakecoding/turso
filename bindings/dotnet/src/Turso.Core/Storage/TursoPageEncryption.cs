using System.Security.Cryptography;

namespace Turso.Core.Storage;

/// <summary>
/// Cipher identifiers 1 and 2 from version 0 of Turso's encrypted page format.
/// Other Turso cipher identifiers are intentionally rejected by managed storage.
/// </summary>
public enum TursoEncryptionCipher : byte
{
    Aes128Gcm = 1,
    Aes256Gcm = 2,
}

/// <summary>
/// Supplies an AES-GCM key for a Turso encrypted SQLite database. The managed
/// storage engine supports only the AES-GCM cipher variants because their page
/// encoding exactly matches the Rust engine and they are provided by .NET.
/// </summary>
public sealed class TursoEncryptionOptions : IDisposable
{
    private byte[]? _key;

    /// <summary>Initializes encryption options from an exact AES key.</summary>
    public TursoEncryptionOptions(TursoEncryptionCipher cipher, ReadOnlySpan<byte> key)
    {
        Cipher = cipher;
        var requiredKeyLength = GetRequiredKeyLength(cipher);
        if (key.Length != requiredKeyLength)
        {
            throw new ArgumentException(
                $"{cipher} requires a {requiredKeyLength}-byte key, but the supplied key has {key.Length} bytes.",
                nameof(key));
        }

        _key = key.ToArray();
    }

    public TursoEncryptionOptions(Enum cipher, ReadOnlySpan<byte> key)
        : this(ConvertCipher(cipher), key)
    {
    }

    /// <summary>The page cipher that will be stored in the Turso encrypted header.</summary>
    public TursoEncryptionCipher Cipher { get; }

    /// <summary>Creates encryption options from Turso's hex-encoded key representation.</summary>
    public static TursoEncryptionOptions FromHex(TursoEncryptionCipher cipher, string hexKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(hexKey);

        try
        {
            var key = Convert.FromHexString(hexKey.Trim());
            try
            {
                return new TursoEncryptionOptions(cipher, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Encryption keys must be hexadecimal.", nameof(hexKey), exception);
        }
    }

    public static TursoEncryptionOptions FromHex<TCipher>(TCipher cipher, string hexKey)
        where TCipher : struct, Enum
    {
        return FromHex(ConvertCipher(cipher), hexKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_key is null)
            return;

        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    internal TursoPageEncryption CreatePageEncryption(int pageSize)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(TursoEncryptionOptions));
        return new TursoPageEncryption(Cipher, key, pageSize);
    }

    internal TursoEncryptionOptions CreateOwnedCopy()
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(TursoEncryptionOptions));
        return new TursoEncryptionOptions(Cipher, key);
    }

    internal static int GetRequiredKeyLength(TursoEncryptionCipher cipher)
        => cipher switch
        {
            TursoEncryptionCipher.Aes128Gcm => 16,
            TursoEncryptionCipher.Aes256Gcm => 32,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cipher),
                cipher,
                "The managed encrypted store supports only Turso AES-GCM cipher IDs 1 and 2."),
        };

    private static TursoEncryptionCipher ConvertCipher(Enum cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        return cipher.ToString() switch
        {
            nameof(TursoEncryptionCipher.Aes128Gcm) => TursoEncryptionCipher.Aes128Gcm,
            nameof(TursoEncryptionCipher.Aes256Gcm) => TursoEncryptionCipher.Aes256Gcm,
            _ => throw new ArgumentOutOfRangeException(
                nameof(cipher),
                cipher,
                "The managed encrypted store supports only Turso AES-GCM cipher IDs 1 and 2."),
        };
    }
}

internal sealed class TursoPageEncryption : IDisposable
{
    internal const int MetadataSize = TagSize + NonceSize;
    internal const int TagSize = 16;
    internal const int NonceSize = 12;
    internal const byte FormatVersion = 0;
    private const int SqliteHeaderSize = 100;
    private const int TursoHeaderSize = 16;

    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;
    private static ReadOnlySpan<byte> TursoHeaderPrefix => "Turso"u8;

    private readonly byte[] _key;
    private bool _disposed;

    public TursoPageEncryption(TursoEncryptionCipher cipher, ReadOnlySpan<byte> key, int pageSize)
    {
        Cipher = cipher;
        PageSize = pageSize;
        if (pageSize <= SqliteHeaderSize + MetadataSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "The page is too small for Turso encryption metadata.");
        if (key.Length != TursoEncryptionOptions.GetRequiredKeyLength(cipher))
            throw new ArgumentException("The encryption key length does not match the configured cipher.", nameof(key));

        _key = key.ToArray();
    }

    public TursoEncryptionCipher Cipher { get; }

    public int PageSize { get; }

    public SqliteDatabaseHeader PrepareHeader(SqliteDatabaseHeader header)
    {
        ThrowIfDisposed();
        if (header.PageSize != PageSize)
            throw new InvalidOperationException("The encryption context and database header page sizes must match.");
        if (header.PageSize - MetadataSize < SqliteDatabaseHeader.MinimumUsableSpace)
            throw new InvalidOperationException("Encryption metadata leaves too little usable SQLite page space.");

        return header with { ReservedSpace = MetadataSize };
    }

    public void ValidateEncryptedHeader(ReadOnlySpan<byte> header)
    {
        ThrowIfDisposed();
        if (header.Length < TursoHeaderSize)
            throw new InvalidDataException("Encrypted Turso database header is truncated.");
        if (!header[..TursoHeaderPrefix.Length].SequenceEqual(TursoHeaderPrefix))
            throw new InvalidDataException("Database does not contain a Turso encrypted header.");
        if (header[5] != FormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Turso encrypted database format version {header[5]}; "
                + "managed storage supports only format version 0 and will not infer or fall back to another format.");
        }
        if (header[6] is not (byte)TursoEncryptionCipher.Aes128Gcm and not (byte)TursoEncryptionCipher.Aes256Gcm)
        {
            throw new InvalidDataException(
                $"Encrypted database uses Turso cipher ID {header[6]} ({GetCipherName(header[6])}); "
                + "managed storage supports only cipher ID 1 (AES-128-GCM) and cipher ID 2 (AES-256-GCM) "
                + "for format version 0 and will not infer or fall back to another cipher.");
        }
        if (header[6] != (byte)Cipher)
        {
            throw new InvalidDataException(
                $"Encrypted database uses Turso cipher ID {header[6]} ({GetCipherName(header[6])}), "
                + $"but the supplied options specify cipher ID {(byte)Cipher} ({GetCipherName((byte)Cipher)}); "
                + "cipher fallback is not permitted.");
        }
        if (header[7..TursoHeaderSize].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Turso encrypted database header has non-zero reserved bytes.");
    }

    public byte[] EncryptPage(ReadOnlySpan<byte> page, uint pageNumber)
    {
        ThrowIfDisposed();
        ValidatePage(page, pageNumber);
        if (page[^MetadataSize..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException(
                $"Plaintext page {pageNumber} uses the {MetadataSize} SQLite reserved bytes required for Turso encryption metadata.");
        }
        if (pageNumber == 1)
            return EncryptFirstPage(page);

        var encrypted = new byte[PageSize];
        var payloadLength = PageSize - MetadataSize;
        Encrypt(
            page[..payloadLength],
            encrypted.AsSpan(..payloadLength),
            encrypted.AsSpan(payloadLength, TagSize),
            encrypted.AsSpan(PageSize - NonceSize, NonceSize),
            []);
        return encrypted;
    }

    public byte[] DecryptPage(ReadOnlySpan<byte> encryptedPage, uint pageNumber)
    {
        ThrowIfDisposed();
        ValidatePage(encryptedPage, pageNumber);
        if (pageNumber == 1)
            return DecryptFirstPage(encryptedPage);

        var plaintext = new byte[PageSize];
        var payloadLength = PageSize - MetadataSize;
        Decrypt(
            encryptedPage[..payloadLength],
            encryptedPage.Slice(payloadLength, TagSize),
            encryptedPage[^NonceSize..],
            plaintext.AsSpan(0, payloadLength),
            [],
            pageNumber);
        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }

    private byte[] EncryptFirstPage(ReadOnlySpan<byte> page)
    {
        if (!page[..SqliteHeader.Length].SequenceEqual(SqliteHeader))
            throw new InvalidDataException("The first plaintext page must contain an SQLite format 3 header.");

        var encrypted = new byte[PageSize];
        TursoHeaderPrefix.CopyTo(encrypted);
        encrypted[5] = FormatVersion;
        encrypted[6] = (byte)Cipher;
        page[TursoHeaderSize..SqliteHeaderSize].CopyTo(encrypted.AsSpan(TursoHeaderSize));

        var payloadLength = PageSize - SqliteHeaderSize - MetadataSize;
        Encrypt(
            page.Slice(SqliteHeaderSize, payloadLength),
            encrypted.AsSpan(SqliteHeaderSize, payloadLength),
            encrypted.AsSpan(PageSize - MetadataSize, TagSize),
            encrypted.AsSpan(PageSize - NonceSize, NonceSize),
            encrypted.AsSpan(0, SqliteHeaderSize));
        return encrypted;
    }

    private byte[] DecryptFirstPage(ReadOnlySpan<byte> encryptedPage)
    {
        ValidateEncryptedHeader(encryptedPage);

        var plaintext = new byte[PageSize];
        SqliteHeader.CopyTo(plaintext);
        encryptedPage[TursoHeaderSize..SqliteHeaderSize].CopyTo(plaintext.AsSpan(TursoHeaderSize));
        var payloadLength = PageSize - SqliteHeaderSize - MetadataSize;
        Decrypt(
            encryptedPage.Slice(SqliteHeaderSize, payloadLength),
            encryptedPage.Slice(PageSize - MetadataSize, TagSize),
            encryptedPage[^NonceSize..],
            plaintext.AsSpan(SqliteHeaderSize, payloadLength),
            encryptedPage[..SqliteHeaderSize],
            pageNumber: 1);
        return plaintext;
    }

    private void Encrypt(
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag,
        Span<byte> nonce,
        ReadOnlySpan<byte> associatedData)
    {
        RandomNumberGenerator.Fill(nonce);
        using var cipher = new AesGcm(_key, TagSize);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
    }

    private void Decrypt(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> nonce,
        Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData,
        uint pageNumber)
    {
        try
        {
            using var cipher = new AesGcm(_key, TagSize);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                $"Encrypted Turso page {pageNumber} failed authentication. The encryption key is incorrect or the file was tampered with.",
                exception);
        }
    }

    private void ValidatePage(ReadOnlySpan<byte> page, uint pageNumber)
    {
        if (pageNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "SQLite page numbers are 1-based.");
        if (page.Length != PageSize)
            throw new ArgumentException($"Encrypted page data must be exactly {PageSize} bytes.", nameof(page));
    }

    private static string GetCipherName(byte cipherId)
        => cipherId switch
        {
            0 => "none",
            1 => "AES-128-GCM",
            2 => "AES-256-GCM",
            3 => "AEGIS-256",
            4 => "AEGIS-256X2",
            5 => "AEGIS-256X4",
            6 => "AEGIS-128L",
            7 => "AEGIS-128X2",
            8 => "AEGIS-128X4",
            _ => "unknown",
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
