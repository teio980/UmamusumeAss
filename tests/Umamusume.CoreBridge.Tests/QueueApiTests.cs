using System.Text.Json;
using Umamusume.CoreBridge.Tests.Fakes;

namespace Umamusume.CoreBridge.Tests;

public sealed class QueueApiTests
{
    [Fact]
    public async Task QueueCallbacksEmittedBeforeStartReturnsAreCorrelatedAndDeliveredInOrder()
    {
        const ulong operationId = 701;
        var dispatcher = new QueuedDispatcher();
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 11,
            StartQueueResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeStartQueueReturn = () =>
        {
            native.Emit(21, Envelope(operationId, 1, "TaskStarted", new
            {
                operation_id = operationId, task_id = 4, name = "mail-collection", index = 0, total_tasks = 1,
            }));
            native.Emit(17, Envelope(operationId, 2, "GameTaskProgress", new
            {
                task_id = "mail-collection", task_instance_id = 4, event_type = 5,
                task_name = "home", pipeline_name = "mail", step_index = 1,
                completed_units = 1, total_units = 2, succeeded = true, score = 0.99,
                target_x = 540, target_y = 960, message = "clicked", timestamp_ms = 1234,
            }));
            native.Emit(22, Envelope(operationId, 3, "TaskDecision", new
            {
                task_id = 4, decision_type = "next", details = "queue continues",
            }));
            native.Emit(23, Envelope(operationId, 4, "TaskActionConfirmed", new
            {
                task_id = 4, action_name = "open-mail", confirmed = true,
            }));
            native.Emit(24, Envelope(operationId, 5, "TaskCompleted", new
            {
                task_id = 4, name = "mail-collection", completed_units = 2, total_units = 2, message = "done",
            }));
            native.Emit(26, Envelope(operationId, 6, "QueueCompleted", new
            {
                succeeded_tasks = 1, failed_tasks = 0, skipped_tasks = 0, total_tasks = 1,
            }));
            // A duplicated terminal is a late event and must not settle the run twice.
            native.Emit(26, Envelope(operationId, 7, "QueueCompleted", new
            {
                succeeded_tasks = 1, failed_tasks = 0, skipped_tasks = 0, total_tasks = 1,
            }));
        };

        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, dispatcher);
            await Initialize(service, root);
            var events = new List<ConnectionEvent>();
            var diagnostics = new List<BridgeDiagnostic>();
            service.ConnectionEventReceived += events.Add;
            service.DiagnosticReceived += diagnostics.Add;

            Task<QueueRunResult> run = service.StartQueueAsync();

            Assert.False(run.IsCompleted);
            Assert.Empty(events);
            dispatcher.Drain();
            QueueRunResult result = await run;

            Assert.Equal(new QueueRunResult(true, 1, 0, 0, 1, ConnectionErrorCode.Success, ""), result);
            Assert.Collection(events,
                item => Assert.IsType<TaskStartedEvent>(item),
                item => Assert.Equal(4UL, Assert.IsType<GameTaskProgressEvent>(item).TaskInstanceId),
                item => Assert.IsType<TaskDecisionEvent>(item),
                item => Assert.IsType<TaskActionConfirmedEvent>(item),
                item => Assert.IsType<TaskCompletedEvent>(item),
                item => Assert.IsType<QueueCompletedEvent>(item));
            Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.LateEvent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueRunSurvivesEventHandlerFailureAndPreservesPartialTaskFailures()
    {
        const ulong operationId = 704;
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 11,
            StartQueueResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeStartQueueReturn = () =>
        {
            native.Emit(21, Envelope(operationId, 1, "TaskStarted", new
            {
                operation_id = operationId, task_id = 31, task_instance_id = 31,
                name = "first-task", index = 0, total_tasks = 2,
            }));
            native.Emit(25, Envelope(operationId, 2, "TaskFailed", new
            {
                operation_id = operationId, task_id = 31, task_instance_id = 31,
                name = "first-task", error_code = (int)ConnectionErrorCode.CommandFailed,
                message = "definition failed",
            }));
            native.Emit(26, Envelope(operationId, 3, "QueueCompleted", new
            {
                operation_id = operationId, succeeded_tasks = 1, failed_tasks = 1,
                skipped_tasks = 0, total_tasks = 2,
            }));
        };

        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);
            var delivered = new List<ConnectionEvent>();
            var diagnostics = new List<BridgeDiagnostic>();
            service.ConnectionEventReceived += connectionEvent =>
            {
                if (connectionEvent is TaskStartedEvent)
                    throw new InvalidOperationException("consumer failure");
                delivered.Add(connectionEvent);
            };
            service.DiagnosticReceived += diagnostics.Add;

            QueueRunResult result = await service.StartQueueAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.SucceededTasks);
            Assert.Equal(1, result.FailedTasks);
            Assert.Equal(0, result.SkippedTasks);
            Assert.Equal(2, result.TotalTasks);
            Assert.Contains(delivered, item => item is TaskFailedEvent);
            Assert.Contains(delivered, item => item is QueueCompletedEvent);
            Assert.Contains(diagnostics, item => item.Category == DiagnosticCategory.DispatcherFailure);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCallbacksAreSuppressedWhenShutdownTimesOutAfterCancellation()
    {
        const ulong operationId = 703;
        var dispatcher = new QueuedDispatcher();
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 11,
            StartQueueResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeStartQueueReturn = () => native.Emit(21, Envelope(operationId, 1, "TaskStarted", new
        {
            operation_id = operationId, task_id = 12, task_instance_id = 12,
            name = "mail-collection", index = 0, total_tasks = 1,
        }));
        string root = CreateRoot();
        try
        {
            var service = CreateService(native, dispatcher);
            await Initialize(service, root);
            var delivered = new List<ConnectionEvent>();
            service.ConnectionEventReceived += delivered.Add;

            Task<QueueRunResult> run = service.StartQueueAsync();
            Assert.False(run.IsCompleted);

            await service.DisposeAsync(TimeSpan.Zero);

            ManagedBridgeException failure = await Assert.ThrowsAsync<ManagedBridgeException>(() => run);
            Assert.Equal(DiagnosticCategory.FatalShutdownTimeout, failure.Category);
            Assert.Equal(new[] { operationId }, native.CanceledOperationIds);
            dispatcher.Drain();
            Assert.Empty(delivered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunQueueReplacesTheWholeQueueAndPassesFrozenSettingsToNative()
    {
        const ulong operationId = 702;
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 11,
            StartQueueResult = new UmaStartResult(operationId, (int)ConnectionErrorCode.Success),
        };
        native.BeforeStartQueueReturn = () => native.Emit(26, Envelope(operationId, 1, "QueueCompleted", new
        {
            succeeded_tasks = 2, failed_tasks = 0, skipped_tasks = 0, total_tasks = 2,
        }));
        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);
            QueueTaskRequest[] tasks =
            [
                new("mail-collection", "{\"limit\":1}"),
                new("career-training", "{\"trainee\":\"春丽\"}"),
            ];

            QueueRunResult result = await service.RunQueueAsync(tasks);

            Assert.True(result.Succeeded);
            Assert.Equal(new[]
            {
                ("mail-collection", "{\"limit\":1}"),
                ("career-training", "{\"trainee\":\"春丽\"}"),
            }, native.AppendedTasks);
            int clearIndex = native.Calls.LastIndexOf("ClearTasks");
            int firstAppend = native.Calls.LastIndexOf("AppendTask");
            int startIndex = native.Calls.LastIndexOf("StartQueueAsync");
            Assert.True(clearIndex >= 0 && firstAppend > clearIndex && startIndex > firstAppend);
            Assert.Equal(2, native.AppendedTasks.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunQueueClearsPartialSubmissionWhenAnAppendFails()
    {
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 11,
        };
        native.AppendTaskResults.Add(new UmaAppendTaskResult(10, (int)ConnectionErrorCode.Success));
        native.AppendTaskResults.Add(new UmaAppendTaskResult(0, (int)ConnectionErrorCode.InvalidArgument));
        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);

            ManagedBridgeException exception = await Assert.ThrowsAsync<ManagedBridgeException>(() =>
                service.RunQueueAsync(
                [new("mail-collection", "{}"), new("unsupported", "{}") ]));

            Assert.Equal((int)ConnectionErrorCode.InvalidArgument, exception.NativeErrorCode);
            Assert.Equal(2, native.Calls.Count(call => call == "ClearTasks"));
            Assert.DoesNotContain("StartQueueAsync", native.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunQueueRejectsEmbeddedNulsBeforeCallingNativeUtf8Api()
    {
        var native = new FakeUmaNativeApi { ApiVersion = 11 };
        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.RunQueueAsync([new("mail\0collection", "{}") ]));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.RunQueueAsync([new("mail-collection", "{\"note\":\"bad\0value\"}") ]));

            Assert.DoesNotContain("ClearTasks", native.Calls);
            Assert.DoesNotContain("AppendTask", native.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunSnapshotRetriesWhenTheRequiredBufferGrowsAndParsesTypedState()
    {
        const string initial = "{\"operation_id\":0,\"state\":\"idle\",\"current_task_id\":0,\"current_task_name\":\"\",\"current_completed_units\":0,\"current_total_units\":0,\"succeeded_count\":0,\"failed_count\":0,\"skipped_count\":0,\"tasks\":[]}";
        const string current = "{\"operation_id\":91,\"state\":\"running\",\"current_task_id\":7,\"current_task_name\":\"career-training\",\"current_completed_units\":2,\"current_total_units\":5,\"succeeded_count\":0,\"failed_count\":0,\"skipped_count\":0,\"tasks\":[{\"id\":7,\"name\":\"career-training\",\"status\":\"running\",\"error_code\":0,\"message\":\"進行中\",\"completed_units\":2,\"total_units\":5}]}";
        var native = new FakeUmaNativeApi { ApiVersion = 11 };
        native.RunSnapshotJsonForCall = call => call == 0 ? initial : current;
        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);

            QueueRunSnapshot snapshot = await service.GetRunSnapshotAsync();

            Assert.Equal(3, native.RunSnapshotJsonCalls);
            Assert.Equal(91UL, snapshot.OperationId);
            Assert.Equal(QueueRunState.Running, snapshot.State);
            Assert.Equal(7UL, snapshot.CurrentTaskId);
            Assert.Equal("career-training", snapshot.CurrentTaskName);
            Assert.Equal(2, snapshot.Tasks[0].CompletedUnits);
            Assert.Equal("進行中", snapshot.Tasks[0].Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunSnapshotRejectsAnExcessiveNativeLengthBeforeAllocating()
    {
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 11,
            RunSnapshotRequiredLengthOverride = 32UL * 1024 * 1024 + 1,
        };
        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);

            ManagedBridgeException exception = await Assert.ThrowsAsync<ManagedBridgeException>(
                () => service.GetRunSnapshotJsonAsync());

            Assert.Equal(DiagnosticCategory.NativeContractViolation, exception.Category);
            Assert.Single(native.Calls, call => call == "GetRunSnapshotJson");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogDtoPreservesFullRowsAndUnknownNativeCategories()
    {
        var native = new FakeUmaNativeApi
        {
            ApiVersion = 11,
            CatalogJson = "{\"database_meta\":{\"version\":3},\"base_characters\":[{\"id\":9,\"name\":\"base\"}],\"characters\":[{\"trainee_id\":1,\"name_en\":\"Mihono Bourbon\",\"aptitudes\":{\"turf\":\"A\"}}],\"races\":[{\"id\":2,\"objectives\":[1]}],\"independent_races\":[{\"id\":4,\"effects\":[\"condition\"]}],\"skills\":[{\"id\":5}],\"skill_metadata\":{\"source\":\"db\"},\"cards\":[{\"support_card_id\":20,\"training_effects\":[1],\"key_effects\":[2]}],\"support_cards\":[{\"support_card_id\":20,\"training_effects\":[1],\"key_effects\":[2]}],\"events\":[{\"id\":3,\"choices\":[\"A\",\"B\"]}],\"event_metadata\":{\"source\":\"db\"},\"strategies\":[{\"id\":6}],\"future_category\":[{\"field\":\"retained\"}]}"
        };
        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);

            CoreCatalog catalog = await service.GetCatalogAsync();

            Assert.Equal("A", catalog.Characters[0].GetProperty("aptitudes").GetProperty("turf").GetString());
            Assert.Equal("base", catalog.BaseCharacters[0].GetProperty("name").GetString());
            Assert.Equal(1, catalog.Races[0].GetProperty("objectives")[0].GetInt32());
            Assert.Equal("condition", catalog.IndependentRaces[0].GetProperty("effects")[0].GetString());
            Assert.Equal(1, catalog.SupportCards[0].GetProperty("training_effects")[0].GetInt32());
            Assert.Equal("B", catalog.Events[0].GetProperty("choices")[1].GetString());
            Assert.Equal(3, catalog.AdditionalCategories["database_meta"].GetProperty("version").GetInt32());
            Assert.Equal("db", catalog.AdditionalCategories["skill_metadata"].GetProperty("source").GetString());
            Assert.Equal("retained", catalog.AdditionalCategories["future_category"][0]
                .GetProperty("field").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCatalogAndSnapshotApisAreRejectedBeforeNativeCallsOnApi10()
    {
        var native = new FakeUmaNativeApi { ApiVersion = 10 };
        string root = CreateRoot();
        try
        {
            await using var service = CreateService(native, new InlineDispatcher());
            await Initialize(service, root);

            await Assert.ThrowsAsync<NotSupportedException>(() => service.AppendTaskAsync("mail-collection", "{}"));
            await Assert.ThrowsAsync<NotSupportedException>(() => service.SetTaskParamsAsync(1, "{}"));
            await Assert.ThrowsAsync<NotSupportedException>(() => service.ClearTasksAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                service.RunQueueAsync([new("mail-collection", "{}") ]));
            await Assert.ThrowsAsync<NotSupportedException>(() => service.StartQueueAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => service.GetRunSnapshotJsonAsync());
            await Assert.ThrowsAsync<NotSupportedException>(() => service.GetCatalogJsonAsync());
            Assert.DoesNotContain("ClearTasks", native.Calls);
            Assert.DoesNotContain("AppendTask", native.Calls);
            Assert.DoesNotContain("SetTaskParams", native.Calls);
            Assert.DoesNotContain("StartQueueAsync", native.Calls);
            Assert.DoesNotContain("GetRunSnapshotJson", native.Calls);
            Assert.DoesNotContain("GetCatalogJson", native.Calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"uma-queue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static UmaService CreateService(FakeUmaNativeApi native, IEventDispatcher dispatcher) =>
        new(native, dispatcher, TimeSpan.FromSeconds(1), TimeProvider.System);

    private static Task Initialize(UmaService service, string root) =>
        service.InitializeAsync(root, Path.Combine(root, "app-data"));

    private static string Envelope(ulong operationId, ulong sequence, string type, object payload) =>
        JsonSerializer.Serialize(new { version = 2, operation_id = operationId, sequence, type, payload });

    private sealed class InlineDispatcher : IEventDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class QueuedDispatcher : IEventDispatcher
    {
        private readonly Queue<Action> _actions = new();

        internal void Drain()
        {
            while (_actions.TryDequeue(out Action? action)) action();
        }

        public void Post(Action action) => _actions.Enqueue(action);
    }
}
