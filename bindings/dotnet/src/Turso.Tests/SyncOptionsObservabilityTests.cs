using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using AwesomeAssertions;
using Turso.Data.Sync;

namespace Turso.Tests;

[NonParallelizable]
public sealed class SyncOptionsObservabilityTests
{
    [Test]
    public void PrefixOptionsMarshalEverySupportedNativeSetting()
    {
        var options = CreateOptions(
            longPollTimeout: TimeSpan.FromMilliseconds(12_345),
            partialBootstrap: TursoPartialBootstrapOptions.Prefix(
                length: 64 * 1024,
                segmentSize: 256 * 1024,
                prefetch: true),
            remoteEncryption: new TursoRemoteEncryptionOptions(
                "c2VjcmV0",
                TursoRemoteEncryptionCipher.Aegis256X2),
            pushOperationsThreshold: 75,
            pullBytesThreshold: 1024 * 1024);

        using var configuration = SyncReplicaConfiguration.Create(options);
        var database = configuration.DatabaseConfig;
        var replica = configuration.ReplicaConfig;

        database.AsyncIo.Should().Be(1);
        ReadUtf8(database.Path).Should().Be(options.Path);
        ReadUtf8(replica.Path).Should().Be(options.Path);
        ReadUtf8(replica.RemoteUrl).Should().Be(options.RemoteUri.AbsoluteUri);
        ReadUtf8(replica.ClientName).Should().Be("turso-dotnet");
        replica.LongPollTimeoutMilliseconds.Should().Be(12_345);
        replica.BootstrapIfEmpty.Should().BeTrue();
        replica.ReservedBytes.Should().Be(48);
        replica.PartialBootstrapStrategyPrefix.Should().Be(64 * 1024);
        replica.PartialBootstrapStrategyQuery.Should().Be(IntPtr.Zero);
        replica.PartialBootstrapSegmentSize.Should().Be((nuint)(256 * 1024));
        replica.PartialBootstrapPrefetch.Should().BeTrue();
        ReadUtf8(replica.RemoteEncryptionKey).Should().Be("c2VjcmV0");
        ReadUtf8(replica.RemoteEncryptionCipher).Should().Be("aegis256x2");
        replica.PushOperationsThreshold.Should().Be((nuint)75);
        replica.PullBytesThreshold.Should().Be((nuint)(1024 * 1024));
        replica.LogicalMvccPull.Should().BeFalse();
    }

    [Test]
    public void QueryOptionsMarshalWithoutUnsupportedPullChunking()
    {
        var options = CreateOptions(
            partialBootstrap: TursoPartialBootstrapOptions.QueryPages(
                "SELECT * FROM users WHERE active",
                segmentSize: 32 * 1024));

        using var configuration = SyncReplicaConfiguration.Create(options);
        var replica = configuration.ReplicaConfig;

        replica.PartialBootstrapStrategyPrefix.Should().Be(0);
        ReadUtf8(replica.PartialBootstrapStrategyQuery)
            .Should().Be("SELECT * FROM users WHERE active");
        replica.PartialBootstrapSegmentSize.Should().Be((nuint)(32 * 1024));
        replica.PullBytesThreshold.Should().Be(0);
        replica.LogicalMvccPull.Should().BeFalse();
    }

    [TestCase(TursoRemoteEncryptionCipher.Aes256Gcm, 28, "aes256gcm")]
    [TestCase(TursoRemoteEncryptionCipher.Aes128Gcm, 28, "aes128gcm")]
    [TestCase(TursoRemoteEncryptionCipher.ChaCha20Poly1305, 28, "chacha20poly1305")]
    [TestCase(TursoRemoteEncryptionCipher.Aegis128L, 32, "aegis128l")]
    [TestCase(TursoRemoteEncryptionCipher.Aegis128X2, 32, "aegis128x2")]
    [TestCase(TursoRemoteEncryptionCipher.Aegis128X4, 32, "aegis128x4")]
    [TestCase(TursoRemoteEncryptionCipher.Aegis256, 48, "aegis256")]
    [TestCase(TursoRemoteEncryptionCipher.Aegis256X2, 48, "aegis256x2")]
    [TestCase(TursoRemoteEncryptionCipher.Aegis256X4, 48, "aegis256x4")]
    public void RemoteEncryptionCipherDeterminesReservedBytesAndNativeName(
        TursoRemoteEncryptionCipher cipher,
        int reservedBytes,
        string nativeName)
    {
        var options = CreateOptions(
            remoteEncryption: new TursoRemoteEncryptionOptions("c2VjcmV0", cipher));

        using var configuration = SyncReplicaConfiguration.Create(options);

        configuration.ReplicaConfig.ReservedBytes.Should().Be(reservedBytes);
        ReadUtf8(configuration.ReplicaConfig.RemoteEncryptionCipher).Should().Be(nativeName);
    }

    [Test]
    public void InvalidOptionCombinationsFailBeforeCallingNativeCode()
    {
        var partialWithoutBootstrap = CreateOptions(
            bootstrapIfEmpty: false,
            partialBootstrap: TursoPartialBootstrapOptions.Prefix(4096));
        var queryWithPullChunks = CreateOptions(
            partialBootstrap: TursoPartialBootstrapOptions.QueryPages("SELECT 1"),
            pullBytesThreshold: 4096);
        var zeroPushThreshold = CreateOptions(pushOperationsThreshold: 0);
        var subMillisecondLongPoll = CreateOptions(longPollTimeout: TimeSpan.FromTicks(1));
        var oversizedLongPoll = CreateOptions(
            longPollTimeout: TimeSpan.FromMilliseconds((double)int.MaxValue + 1));

        Action createPartialWithoutBootstrap = () => SyncReplicaConfiguration.Create(partialWithoutBootstrap);
        Action createQueryWithPullChunks = () => SyncReplicaConfiguration.Create(queryWithPullChunks);
        Action createZeroPushThreshold = () => SyncReplicaConfiguration.Create(zeroPushThreshold);
        Action createSubMillisecondLongPoll = () => SyncReplicaConfiguration.Create(subMillisecondLongPoll);
        Action createOversizedLongPoll = () => SyncReplicaConfiguration.Create(oversizedLongPoll);

        createPartialWithoutBootstrap.Should().Throw<InvalidOperationException>()
            .WithMessage("Partial bootstrap requires BootstrapIfEmpty=True*");
        createQueryWithPullChunks.Should().Throw<InvalidOperationException>()
            .WithMessage("PullBytesThreshold cannot be combined with query partial bootstrap*");
        createZeroPushThreshold.Should().Throw<ArgumentOutOfRangeException>();
        createSubMillisecondLongPoll.Should().Throw<ArgumentOutOfRangeException>();
        createOversizedLongPoll.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void PartialAndHttpPolicyFactoriesRejectInvalidValues()
    {
        Action zeroPrefix = () => TursoPartialBootstrapOptions.Prefix(0);
        Action emptyQuery = () => TursoPartialBootstrapOptions.QueryPages(" ");
        Action zeroSegment = () => TursoPartialBootstrapOptions.Prefix(4096, segmentSize: 0);
        Action zeroTimeout = () => new TursoSyncHttpPolicy(requestTimeout: TimeSpan.Zero);
        Action oversizedTimeout = () => new TursoSyncHttpPolicy(
            requestTimeout: TimeSpan.FromMilliseconds((double)int.MaxValue + 1));

        zeroPrefix.Should().Throw<ArgumentOutOfRangeException>();
        emptyQuery.Should().Throw<ArgumentException>();
        zeroSegment.Should().Throw<ArgumentOutOfRangeException>();
        zeroTimeout.Should().Throw<ArgumentOutOfRangeException>();
        oversizedTimeout.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void NativeStatisticsMapToStableManagedOutcome()
    {
        using var revision = NativeUtf8String.From("revision-42");
        var native = new SyncStats
        {
            CdcOperations = 7,
            MainWalSize = 11,
            RevertWalSize = 13,
            LastPullUnixTime = 1_700_000_000,
            LastPushUnixTime = 1_700_000_100,
            NetworkSentBytes = 17,
            NetworkReceivedBytes = 19,
            Revision = new SyncSlice
            {
                Pointer = revision.Pointer,
                Length = (nuint)Encoding.UTF8.GetByteCount("revision-42"),
            },
        };

        var statistics = SyncNative.CopyStatistics(native);
        var upToDate = SyncNative.CreateResult(changesApplied: false, statistics);
        var applied = SyncNative.CreateResult(changesApplied: true, statistics);

        statistics.CdcOperations.Should().Be(7);
        statistics.MainWalSize.Should().Be(11);
        statistics.RevertWalSize.Should().Be(13);
        statistics.LastPull.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        statistics.LastPush.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_700_000_100));
        statistics.NetworkSentBytes.Should().Be(17);
        statistics.NetworkReceivedBytes.Should().Be(19);
        statistics.Revision.Should().Be("revision-42");
        upToDate.Outcome.Should().Be(TursoSyncOutcome.UpToDate);
        applied.Outcome.Should().Be(TursoSyncOutcome.RemoteChangesApplied);
        applied.Statistics.Should().BeSameAs(statistics);
    }

    [Test]
    public void LegacySyncApiSignaturesRemainBinaryCompatible()
    {
        typeof(TursoConnection)
            .GetMethod(nameof(TursoConnection.Sync), Type.EmptyTypes)!
            .ReturnType.Should().Be(typeof(void));
        typeof(TursoConnection)
            .GetMethod(nameof(TursoConnection.SyncAsync), [typeof(CancellationToken)])!
            .ReturnType.Should().Be(typeof(Task));

        var providerMethod = typeof(TursoReplicaDatabase)
            .GetMethod(nameof(TursoReplicaDatabase.SyncAsync), [typeof(CancellationToken)])!;
        providerMethod.ReturnType.Should().Be(typeof(Task));
        providerMethod.IsAbstract.Should().BeTrue();
    }

    [Test]
    public void MissingNativeStatisticsValuesMapToNull()
    {
        var statistics = SyncNative.CopyStatistics(default);

        statistics.LastPull.Should().BeNull();
        statistics.LastPush.Should().BeNull();
        statistics.Revision.Should().BeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void HttpPolicyControlsMessageHandlerOwnership(bool disposeHandler)
    {
        var handler = new TrackingHandler();
        var options = CreateOptions(
            bootstrapIfEmpty: false,
            httpPolicy: new TursoSyncHttpPolicy(handler, disposeHandler));

        using (SyncReplicaDatabase.Open(options))
        {
        }

        handler.IsDisposed.Should().Be(disposeHandler);
        handler.Dispose();
    }

    [Test]
    public async Task HttpPolicyTimeoutFailsWithoutReportingUserCancellation()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TrackingHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timed-out request unexpectedly completed.");
        });
        var options = CreateOptions(
            bootstrapIfEmpty: false,
            httpPolicy: new TursoSyncHttpPolicy(
                handler,
                requestTimeout: TimeSpan.FromMilliseconds(50)));
        using var replica = SyncReplicaDatabase.Open(options);

        var sync = replica.SyncAsync(CancellationToken.None);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var synchronize = async () => await sync.WaitAsync(TimeSpan.FromSeconds(5));

        await synchronize.Should().ThrowAsync<TursoException>();
        sync.IsCanceled.Should().BeFalse();
    }

    [Test]
    public async Task UserCancellationStopsProgressBeforeCompletion()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TrackingHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The canceled request unexpectedly completed.");
        });
        var options = CreateOptions(
            bootstrapIfEmpty: false,
            httpPolicy: new TursoSyncHttpPolicy(handler));
        using var replica = SyncReplicaDatabase.Open(options);
        using var cancellation = new CancellationTokenSource();
        var stages = new List<TursoSyncProgressStage>();
        var sync = replica.SyncAsync(
            new TursoSyncOptions(new InlineProgress(value => stages.Add(value.Stage))),
            cancellation.Token);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var synchronize = async () => await sync.WaitAsync(TimeSpan.FromSeconds(5));

        await synchronize.Should().ThrowAsync<OperationCanceledException>();
        sync.IsCanceled.Should().BeTrue();
        stages.Should().Equal(TursoSyncProgressStage.Pushing);
    }

    [Test]
    public async Task ProgressReportsCompletedPhasesAndStopsAtPullFailure()
    {
        using var handler = new SequencedHandler(
            JsonResponse(
                """
                {
                  "results": [
                    {
                      "type": "error",
                      "error": {
                        "message": "no such table: turso_sync_last_change_id",
                        "code": "SQLITE_ERROR"
                      }
                    }
                  ]
                }
                """),
            JsonResponse(
                """
                {
                  "results": [
                    {
                      "type": "ok",
                      "response": {
                        "type": "batch",
                        "result": {
                          "step_results": [],
                          "step_errors": []
                        }
                      }
                    }
                  ]
                }
                """),
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("synthetic pull failure"),
            });
        var options = CreateOptions(
            bootstrapIfEmpty: false,
            httpPolicy: new TursoSyncHttpPolicy(handler));
        using var replica = SyncReplicaDatabase.Open(options);
        var stages = new List<TursoSyncProgressStage>();
        var progress = new InlineProgress(value => stages.Add(value.Stage));

        var synchronize = async () => await replica.SyncAsync(
            new TursoSyncOptions(progress),
            CancellationToken.None);

        await synchronize.Should().ThrowAsync<TursoException>();
        stages.Should().Equal(TursoSyncProgressStage.Pushing, TursoSyncProgressStage.Pulling);
    }

    [Test]
    public async Task ProgressCallbackCannotReenterReplicaOperations()
    {
        using var handler = new TrackingHandler();
        var options = CreateOptions(
            bootstrapIfEmpty: false,
            httpPolicy: new TursoSyncHttpPolicy(handler));
        using var replica = SyncReplicaDatabase.Open(options);
        var progress = new InlineProgress(_ =>
        {
            using var statement = replica.PrepareStatement("SELECT 1");
        });

        var synchronize = async () => await replica.SyncAsync(
            new TursoSyncOptions(progress),
            CancellationToken.None);

        await synchronize.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Embedded replica operations cannot be reentered from a sync progress callback.");
        using var statement = replica.PrepareStatement("SELECT 42");
        statement.Read().Should().BeTrue();
        statement.GetValue(0).IntValue.Should().Be(42);
    }

    [Test]
    public async Task ProgressCallbackCannotDisposeReplica()
    {
        using var handler = new TrackingHandler();
        var options = CreateOptions(
            bootstrapIfEmpty: false,
            httpPolicy: new TursoSyncHttpPolicy(handler));
        var replica = SyncReplicaDatabase.Open(options);
        try
        {
            var progress = new InlineProgress(_ => replica.Dispose());
            var synchronize = async () => await replica.SyncAsync(
                new TursoSyncOptions(progress),
                CancellationToken.None);

            await synchronize.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("An embedded replica cannot be closed from a sync progress callback.");
            using var statement = replica.PrepareStatement("SELECT 42");
            statement.Read().Should().BeTrue();
            statement.GetValue(0).IntValue.Should().Be(42);
        }
        finally
        {
            replica.Dispose();
        }
    }

    private static TursoReplicaOptions CreateOptions(
        bool bootstrapIfEmpty = true,
        TimeSpan? longPollTimeout = null,
        TursoPartialBootstrapOptions? partialBootstrap = null,
        TursoRemoteEncryptionOptions? remoteEncryption = null,
        long? pushOperationsThreshold = null,
        long? pullBytesThreshold = null,
        TursoSyncHttpPolicy? httpPolicy = null)
        => new(
            ":memory:",
            new Uri("http://127.0.0.1"),
            authToken: null,
            bootstrapIfEmpty)
        {
            LongPollTimeout = longPollTimeout,
            PartialBootstrap = partialBootstrap,
            RemoteEncryption = remoteEncryption,
            PushOperationsThreshold = pushOperationsThreshold,
            PullBytesThreshold = pullBytesThreshold,
            HttpPolicy = httpPolicy ?? new TursoSyncHttpPolicy(),
        };

    private static string? ReadUtf8(IntPtr value)
        => value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class InlineProgress(Action<TursoSyncProgress> report) : IProgress<TursoSyncProgress>
    {
        public void Report(TursoSyncProgress value) => report(value);
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public TrackingHandler()
            : this(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
        {
        }

        public TrackingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _send(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            if (index >= responses.Length)
                throw new InvalidOperationException($"Unexpected HTTP request {index + 1}: {request.RequestUri}.");
            return Task.FromResult(responses[index]);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in responses)
                    response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
