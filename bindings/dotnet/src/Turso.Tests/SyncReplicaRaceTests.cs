using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Turso.Data.Sync;

namespace Turso.Tests;

[NonParallelizable]
public sealed class SyncReplicaRaceTests
{
    [Test]
    public async Task ConcurrentSyncCallsAreSingleFlightAndRecoverAfterCancellation()
    {
        using var handler = new ControlledHandler(
            static (_, _, cancellationToken) => HoldRequestAsync(cancellationToken));
        using var replica = OpenReplica(handler);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = replica.SyncAsync(firstCancellation.Token);
        await handler.WaitForRequestAsync(1);
        var second = replica.SyncAsync(secondCancellation.Token);

        await Task.Delay(100);
        handler.RequestCount.Should().Be(1);

        await firstCancellation.CancelAsync();
        await AssertCanceledAsync(first);
        await handler.WaitForRequestAsync(2);
        await secondCancellation.CancelAsync();
        await AssertCanceledAsync(second);

        handler.MaximumActiveRequests.Should().Be(1);
    }

    [Test]
    public async Task DisposingReplicaCancelsPendingHttpIoAndWaitsForCompletion()
    {
        using var handler = new ControlledHandler(
            static (_, _, cancellationToken) => HoldRequestAsync(cancellationToken));
        var replica = OpenReplica(handler);
        var sync = replica.SyncAsync(CancellationToken.None);
        await handler.WaitForRequestAsync(1);

        var dispose = Task.Run(replica.Dispose);

        await AssertCanceledAsync(sync);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        var synchronize = async () => await replica.SyncAsync(CancellationToken.None);
        await synchronize.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task StreamingFailureCompletesNativeIoAndAllowsAnotherSyncAttempt()
    {
        using var handler = new ControlledHandler((request, _, cancellationToken) =>
        {
            if (request == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ThrowingReadStream()),
                });
            }

            return HoldRequestAsync(cancellationToken);
        });
        using var replica = OpenReplica(handler);

        var first = async () => await replica.SyncAsync(CancellationToken.None);
        await first.Should().ThrowAsync<TursoException>()
            .WithMessage("*synthetic streaming failure*");

        using var cancellation = new CancellationTokenSource();
        var retry = replica.SyncAsync(cancellation.Token);
        await handler.WaitForRequestAsync(2);
        await cancellation.CancelAsync();
        await AssertCanceledAsync(retry);
    }

    [Test]
    public async Task FailedPullAfterSuccessfulPushLeavesReplicaUsableAndRetryable()
    {
        using var handler = new ControlledHandler((request, _, cancellationToken) => request switch
        {
            1 => Task.FromResult(JsonResponse(
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
                """)),
            2 => Task.FromResult(JsonResponse(
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
                """)),
            3 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("synthetic pull failure"),
            }),
            _ => HoldRequestAsync(cancellationToken),
        });
        using var replica = OpenReplica(handler);

        var synchronize = async () => await replica.SyncAsync(CancellationToken.None);
        await synchronize.Should().ThrowAsync<TursoException>();
        handler.RequestPaths.Should().HaveCount(3);
        handler.RequestPaths[0].Should().EndWith("/v2/pipeline");
        handler.RequestPaths[1].Should().EndWith("/v2/pipeline");
        handler.RequestPaths[2].Should().Contain("/pull-updates");

        using (var statement = replica.PrepareStatement("SELECT 42;"))
        {
            statement.Read().Should().BeTrue();
            statement.GetValue(0).IntValue.Should().Be(42);
        }

        using var cancellation = new CancellationTokenSource();
        var retry = replica.SyncAsync(cancellation.Token);
        await handler.WaitForRequestAsync(4);
        await cancellation.CancelAsync();
        await AssertCanceledAsync(retry);
    }

    [Test]
    public async Task ReplicaCanReopenAfterPoisonedSyncOperation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"turso-sync-reopen-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "replica.db");
        Directory.CreateDirectory(directory);
        var options = new TursoReplicaOptions(
            path,
            new Uri("http://127.0.0.1"),
            authToken: null,
            bootstrapIfEmpty: false);

        try
        {
            using (var failingHandler = new ControlledHandler(
                       static (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                       {
                           Content = new StringContent("synthetic transport failure"),
                       })))
            using (var replica = SyncReplicaDatabase.Open(options, failingHandler))
            {
                var synchronize = async () => await replica.SyncAsync(CancellationToken.None);
                await synchronize.Should().ThrowAsync<TursoException>();
            }

            using var reopenedHandler = new ControlledHandler(
                static (_, _, cancellationToken) => HoldRequestAsync(cancellationToken));
            using var reopened = SyncReplicaDatabase.Open(options, reopenedHandler);
            using var statement = reopened.PrepareStatement("SELECT 42;");
            statement.Read().Should().BeTrue();
            statement.GetValue(0).IntValue.Should().Be(42);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task CanceledAtomicFileWritePreservesDestinationAndRemovesTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"turso-sync-write-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "replica.db");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "original");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try
        {
            var write = async () => await SyncReplicaDatabase.WriteFileAtomicallyAsync(
                path,
                new byte[1024 * 1024],
                cancellation.Token);

            await write.Should().ThrowAsync<OperationCanceledException>();
            (await File.ReadAllTextAsync(path)).Should().Be("original");
            Directory.EnumerateFiles(directory, "replica.db.*.tmp").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SyncReplicaDatabase OpenReplica(HttpMessageHandler handler)
    {
        return SyncReplicaDatabase.Open(
            new TursoReplicaOptions(
                ":memory:",
                new Uri("http://127.0.0.1"),
                authToken: null,
                bootstrapIfEmpty: false),
            handler);
    }

    private static async Task<HttpResponseMessage> HoldRequestAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The held request completed without cancellation.");
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };

    private static async Task AssertCanceledAsync(Task task)
    {
        var exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
        exception.Should().NotBeNull();
        task.IsCanceled.Should().BeTrue();
    }

    private sealed class ControlledHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        private readonly ConcurrentQueue<string> _requestPaths = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _requests = new();
        private int _activeRequests;
        private int _maximumActiveRequests;
        private int _requestCount;

        public ControlledHandler(
            Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public int MaximumActiveRequests => Volatile.Read(ref _maximumActiveRequests);

        public IReadOnlyList<string> RequestPaths => _requestPaths.ToArray();

        public Task WaitForRequestAsync(int request)
            => GetRequest(request).Task.WaitAsync(TimeSpan.FromSeconds(5));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            var activeRequests = Interlocked.Increment(ref _activeRequests);
            UpdateMaximumActiveRequests(activeRequests);
            _requestPaths.Enqueue(request.RequestUri?.AbsolutePath ?? string.Empty);
            GetRequest(requestNumber).TrySetResult();
            try
            {
                return await _handler(requestNumber, request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private TaskCompletionSource GetRequest(int request)
            => _requests.GetOrAdd(
                request,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private void UpdateMaximumActiveRequests(int activeRequests)
        {
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumActiveRequests);
                if (activeRequests <= maximum)
                    return;
                if (Interlocked.CompareExchange(ref _maximumActiveRequests, activeRequests, maximum) == maximum)
                    return;
            }
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("synthetic streaming failure");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("synthetic streaming failure"));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
