using LabCsharpAsyncMemory.Api;
using Microsoft.OpenApi;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LabPool>();
builder.Services.AddSingleton<BufferTracker>();
builder.Services.AddScoped<ReportBuffer>();

builder.Services.AddOpenApi(options =>
{
    _ = options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "lab-csharp-async-memory";
        document.Info.Version = "v1";
        document.Info.Description =
            """
            Where a wait holds a worker, where a token stops the loop, and what a request still owes after it returns 200.

            GET /async/wait-burst compares waitMode=await with waitMode=block. await does not hold a worker during the delay. block holds one with GetResult. Do not copy the blocking path into a real API.

            GET /async/cancellable-wait runs for durationMs and checks the token between steps. cancelAfterMs fires that token without closing the browser.

            GET /memory/scoped-buffer disposes the buffer with the scope. GET /memory/orphaned-buffer does not. GET /memory/string-concat and GET /memory/string-builder build the same text at different allocation cost.
            """;
        return Task.CompletedTask;
    });

    // Swagger UI rejects a required integer when OpenAPI emits type ["integer", "string"].
    _ = options.AddSchemaTransformer((schema, _, _) =>
    {
        if (schema.Type is JsonSchemaType type
            && type.HasFlag(JsonSchemaType.Integer)
            && type.HasFlag(JsonSchemaType.String))
        {
            JsonSchemaType integer = JsonSchemaType.Integer;
            if (type.HasFlag(JsonSchemaType.Null))
            {
                integer |= JsonSchemaType.Null;
            }

            schema.Type = integer;
        }

        return Task.CompletedTask;
    });
});

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
    _ = app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "lab-csharp-async-memory v1");
        options.DocumentTitle = "lab-csharp-async-memory";
        options.DisplayRequestDuration();
        options.EnableTryItOutByDefault();
    });
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithTags("Health")
    .WithSummary("Liveness probe")
    .WithDescription("Returns 200 when the process can serve HTTP. It does not run a wait or allocate a buffer.")
    .AllowAnonymous();

app.MapGet("/async/wait-burst", async (
        [Description("How the wait is observed. await releases the worker during the delay. block holds a worker with GetResult. Required.")] string waitMode,
        [Description("How many waits to run. Required. From 1 to 32. The guide uses 16.")] int waitCount,
        LabPool pool,
        CancellationToken cancellationToken) =>
    {
        if (waitMode is not ("await" or "block"))
        {
            return QueryProblems.Invalid(
                "waitMode",
                "waitMode must be await or block. await releases the worker during the delay. block holds a worker with GetResult.");
        }

        if (waitCount is < 1 or > 32)
        {
            return QueryProblems.Invalid(
                "waitCount",
                "waitCount must be from 1 to 32. The guide compares 16 waits.");
        }

        BurstResult result = await pool.RunAsync(waitMode, waitCount, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("GetWaitBurst")
    .WithTags("Async")
    .WithSummary("Compare an awaited delay with a blocking delay")
    .WithDescription(
        "Runs waitCount delays of 150 milliseconds on a lab pool of two workers. " +
        "waitMode=await leaves peakThreads at 0. waitMode=block keeps a worker for each delay, so peakThreads is 2 and elapsedMs is several times longer. " +
        "Do not copy waitMode=block into a real API.")
    .Produces<BurstResult>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest);

app.MapGet("/async/cancellable-wait", async (
        [Description("How long the handler should keep working, in milliseconds. Required. From 1 to 10000.")] int durationMs,
        [Description("Cancel the wait this many milliseconds after it starts. Optional. From 1 to 10000. Omit it to let durationMs finish.")] int? cancelAfterMs,
        CancellationToken requestAborted) =>
    {
        if (durationMs is < 1 or > 10000)
        {
            return QueryProblems.Invalid(
                "durationMs",
                "durationMs must be from 1 to 10000. It is the length of the wait in milliseconds. Example: durationMs=400.");
        }

        if (cancelAfterMs is < 1 or > 10000)
        {
            return QueryProblems.Invalid(
                "cancelAfterMs",
                "cancelAfterMs must be from 1 to 10000 when it is sent. It cancels the wait that many milliseconds after the call starts. Omit it to let durationMs finish.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        if (cancelAfterMs is int afterMs)
        {
            linked.CancelAfter(afterMs);
        }

        Stopwatch clock = Stopwatch.StartNew();
        int waited = 0;
        try
        {
            while (waited < durationMs)
            {
                linked.Token.ThrowIfCancellationRequested();
                int slice = Math.Min(50, durationMs - waited);
                await Task.Delay(slice, linked.Token);
                waited += slice;
            }

            return Results.Ok(new WorkResult(true, false, clock.ElapsedMilliseconds));
        }
        catch (OperationCanceledException)
        {
            return Results.Ok(new WorkResult(false, true, clock.ElapsedMilliseconds));
        }
    })
    .WithName("GetCancellableWait")
    .WithTags("Async")
    .WithSummary("Run a wait that stops when its token is cancelled")
    .WithDescription(
        "The handler checks the token between steps of at most 50 milliseconds. " +
        "durationMs=4000 and cancelAfterMs=200 returns completed false and cancelled true, with elapsedMs well under 4000. " +
        "durationMs=400 without cancelAfterMs finishes the wait. A token that the loop never reads would not stop it.")
    .Produces<WorkResult>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest);

app.MapGet("/memory/scoped-buffer", async (IServiceScopeFactory scopes, BufferTracker tracker) =>
    {
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<ReportBuffer>();
        }

        return Results.Ok(new BufferResult(tracker.Live));
    })
    .WithName("GetScopedBuffer")
    .WithTags("Memory")
    .WithSummary("Create a scoped buffer and dispose it with the scope")
    .WithDescription("liveBuffers after the call does not include this buffer. A second call reports the same liveBuffers.")
    .Produces<BufferResult>(StatusCodes.Status200OK);

app.MapGet("/memory/orphaned-buffer", (BufferTracker tracker) =>
    {
        _ = new ReportBuffer(tracker);
        return Results.Ok(new BufferResult(tracker.Live));
    })
    .WithName("GetOrphanedBuffer")
    .WithTags("Memory")
    .WithSummary("Create a buffer and do not dispose it")
    .WithDescription("The response is still 200. liveBuffers rises by one on every call because Dispose never runs.")
    .Produces<BufferResult>(StatusCodes.Status200OK);

app.MapGet("/memory/string-concat", (
        [Description("How many one-character pieces to append. Required. From 1 to 5000. The guide uses 2000.")] int partCount) =>
    {
        if (partCount is < 1 or > 5000)
        {
            return QueryProblems.Invalid(
                "partCount",
                "partCount must be from 1 to 5000. It is how many characters are appended. The guide uses partCount=2000.");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        string text = "";
        for (int index = 0; index < partCount; index++)
        {
            text += "a";
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return Results.Ok(new AllocationResult(text.Length, allocated));
    })
    .WithName("GetStringConcat")
    .WithTags("Memory")
    .WithSummary("Build a string by appending one character at a time")
    .WithDescription(
        "Each append copies the text so far into a new string. " +
        "Compare allocatedBytes with GET /memory/string-builder for the same partCount. length matches. allocatedBytes does not.")
    .Produces<AllocationResult>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest);

app.MapGet("/memory/string-builder", (
        [Description("How many one-character pieces to append. Required. From 1 to 5000. The guide uses 2000.")] int partCount) =>
    {
        if (partCount is < 1 or > 5000)
        {
            return QueryProblems.Invalid(
                "partCount",
                "partCount must be from 1 to 5000. It is how many characters are appended. The guide uses partCount=2000.");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        StringBuilder builderText = new();
        for (int index = 0; index < partCount; index++)
        {
            _ = builderText.Append('a');
        }

        string text = builderText.ToString();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return Results.Ok(new AllocationResult(text.Length, allocated));
    })
    .WithName("GetStringBuilder")
    .WithTags("Memory")
    .WithSummary("Build the same string with a StringBuilder")
    .WithDescription(
        "The builder grows one buffer and copies once at the end. " +
        "length matches GET /memory/string-concat for the same partCount. allocatedBytes stays much lower.")
    .Produces<AllocationResult>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest);

app.Run();

public partial class Program;

internal static class QueryProblems
{
    public static IResult Invalid(string name, string message)
    {
        return Results.ValidationProblem(
            new Dictionary<string, string[]> { [name] = [message] },
            title: "The query is not valid.",
            detail: message);
    }
}
