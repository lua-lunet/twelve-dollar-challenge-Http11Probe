using System.CommandLine;
using Http11Probe.Cli.Reporting;
using Http11Probe.Challenge;

var hostOption = new Option<string>("--host") { Description = "Server hostname or IP address" };
hostOption.DefaultValueFactory = _ => "127.0.0.1";

var portOption = new Option<int>("--port") { Description = "Server port number" };
portOption.DefaultValueFactory = _ => 3000;

var tokenOption = new Option<string>("--token") { Description = "A valid bearer token for the seed user (HS256, JWT_SECRET)" };
tokenOption.Required = true;

var knownPostOption = new Option<long>("--known-post-id") { Description = "A post id that exists on the server (used for GET /posts/:id and the like steps)" };
knownPostOption.Required = true;

var endpointOption = new Option<string>("--category") { Description = "Run only this endpoint: health | feed | posts-read | posts-create | posts-like" };

var testOption = new Option<string[]>("--test") { Description = "Run only specific test step IDs, case-insensitive (repeatable)", Arity = ArgumentArity.OneOrMore };

var timeoutOption = new Option<int>("--timeout") { Description = "Connect and read timeout in seconds per step" };
timeoutOption.DefaultValueFactory = _ => 5;

var outputOption = new Option<string?>("--output") { Description = "Write JSON results to this file path" };

var profileOption = new Option<string>("--profile") { Description = "Judgement lens for the transport fuzz steps: generic (RFC allow-lists, judges any server) | subset (lunet transport subset: hard 501 for TE: chunked, hard 400 for HTTP/1.0 and pipelining)" };
profileOption.AcceptOnlyFromAmong(ProbeProfile.Generic, ProbeProfile.Subset);
profileOption.DefaultValueFactory = _ => ProbeProfile.Generic;

var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print the raw request/response for each step" };
verboseOption.DefaultValueFactory = _ => false;

var rootCommand = new RootCommand("twelve-dollar-challenge-Http11Probe — fuzzes the five official challenge endpoints, bad requests first, good request last, fail-fast per endpoint")
{
    hostOption,
    portOption,
    tokenOption,
    knownPostOption,
    endpointOption,
    testOption,
    timeoutOption,
    outputOption,
    profileOption,
    verboseOption
};

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var host = parseResult.GetValue(hostOption)!;
    var port = parseResult.GetValue(portOption);
    var token = parseResult.GetValue(tokenOption)!;
    var knownPostId = parseResult.GetValue(knownPostOption);
    var category = parseResult.GetValue(endpointOption);
    var testIds = parseResult.GetValue(testOption);
    var timeout = parseResult.GetValue(timeoutOption);
    var outputPath = parseResult.GetValue(outputOption);
    var profile = parseResult.GetValue(profileOption)!;
    var verbose = parseResult.GetValue(verboseOption);

    Console.WriteLine($"  Http11Probe targeting {host}:{port} — profile: {profile}");
    Console.WriteLine();

    var options = new ProbeOptions
    {
        Host = host,
        Port = port,
        Token = token,
        KnownPostId = knownPostId,
        ConnectTimeout = TimeSpan.FromSeconds(timeout),
        ReadTimeout = TimeSpan.FromSeconds(timeout),
        Profile = profile
    };

    var endpoints = ChallengeEndpointSuite.GetEndpoints(options).ToList();
    if (category is { Length: > 0 } cat)
        endpoints = endpoints.Where(e => e.Name.Equals(cat, StringComparison.OrdinalIgnoreCase)).ToList();
    if (testIds is { Length: > 0 } testIdArgs)
    {
        var ids = testIdArgs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        endpoints = endpoints
            .Select(e => new ChallengeEndpoint
            {
                Name = e.Name,
                Description = e.Description,
                Steps = e.Steps.Where(s => ids.Contains(s.Id)).ToArray(),
                SkippedReasons = e.SkippedReasons
            })
            .Where(e => e.Steps.Count > 0)
            .ToList();
    }

    var runner = new ChallengeRunner(options);
    var report = await runner.RunAsync(endpoints, e =>
    {
        ConsoleReporter.PrintEndpointHeader(e);
    }, o =>
    {
        ConsoleReporter.PrintStep(o);
        if (verbose)
            ConsoleReporter.PrintRaw(o);
    });
    foreach (var e in report.Endpoints)
        ConsoleReporter.PrintEndpointTrailer(e);
    ConsoleReporter.PrintSummary(report);

    if (outputPath is not null)
    {
        var json = JsonReporter.Generate(report);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken);
        Console.WriteLine($"  JSON report written to {outputPath}");
    }
});

var config = new CommandLineConfiguration(rootCommand);
return await config.InvokeAsync(args);
