using System.Runtime.InteropServices;
using System.Text.Json;
using WorkspaceRuntime.Application;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var planner = new InstallationPlanner();

try
{
    if (args.Length == 0)
    {
        return Fail("A command is required: defaults, probe, plan, validate, approve, or create-owner.", 2);
    }

    switch (args[0])
    {
        case "defaults":
            Write(planner.GetDefaults());
            return 0;

        case "probe":
            Write(ProbeSystem());
            return 0;

        case "plan":
        {
            var requestPath = GetOption(args, "--request");
            var probePath = GetOption(args, "--probe");
            var request = requestPath is null
                ? new InstallationRequest()
                : Read<InstallationRequest>(requestPath);
            var probe = probePath is null ? ProbeSystem() : Read<InstallationProbe>(probePath);
            Write(planner.CreatePlan(request, probe));
            return 0;
        }

        case "validate":
        {
            var plan = Read<InstallationPlan>(RequireOption(args, "--plan"));
            var probePath = GetOption(args, "--probe");
            var probe = probePath is null ? ProbeSystem() : Read<InstallationProbe>(probePath);
            var result = planner.Validate(plan, probe);
            Write(result);
            return result.IsValid ? 0 : 3;
        }

        case "approve":
        {
            var plan = Read<InstallationPlan>(RequireOption(args, "--plan"));
            var probePath = GetOption(args, "--probe");
            var probe = probePath is null ? ProbeSystem() : Read<InstallationProbe>(probePath);
            var token = RequireOption(args, "--token");
            var approvedBy = RequireOption(args, "--approved-by");
            Write(planner.Approve(plan, probe, token, approvedBy, DateTimeOffset.UtcNow));
            return 0;
        }

        case "create-owner":
        {
            // Claim the first owner on THIS machine. Runs on the box, so the POST
            // reaches the runtime over loopback — the only origin the claim
            // accepts while unclaimed. Prints the owner's bearer token to stdout.
            var name = RequireOption(args, "--name");
            var url = (GetOption(args, "--url") ?? "http://127.0.0.1:5148").TrimEnd('/');
            return ClaimOwner(name, url);
        }

        default:
            return Fail($"Unknown command '{args[0]}'.", 2);
    }
}
catch (UnauthorizedAccessException exception)
{
    return Fail(exception.Message, 4);
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or JsonException)
{
    return Fail(exception.Message, 3);
}

void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, json));

T Read<T>(string path)
{
    var content = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(path);
    return JsonSerializer.Deserialize<T>(content, json)
        ?? throw new JsonException($"Could not parse {typeof(T).Name}.");
}

static string? GetOption(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static string RequireOption(string[] arguments, string name) =>
    GetOption(arguments, name) ?? throw new ArgumentException($"Required option is missing: {name}");

static int Fail(string message, int exitCode)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = message, exitCode }));
    return exitCode;
}

static int ClaimOwner(string name, string url)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var payload = new StringContent(
        JsonSerializer.Serialize(new { name }),
        System.Text.Encoding.UTF8, "application/json");

    HttpResponseMessage response;
    try
    {
        response = client.PostAsync($"{url}/api/setup/claim", payload).GetAwaiter().GetResult();
    }
    catch (HttpRequestException exception)
    {
        return Fail($"Could not reach the runtime at {url}: {exception.Message}. Is it running?", 5);
    }

    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    if (response.IsSuccessStatusCode)
    {
        using var document = JsonDocument.Parse(body);
        var slug = document.RootElement.GetProperty("slug").GetString();
        var token = document.RootElement.GetProperty("token").GetString();
        Console.WriteLine(JsonSerializer.Serialize(new { owner = slug, token }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    var reason = TryReadError(body) ?? $"HTTP {(int)response.StatusCode}";
    return Fail($"Claim failed: {reason}", 3);
}

static string? TryReadError(string body)
{
    try
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static InstallationProbe ProbeSystem()
{
    var memoryMiB = 0L;
    if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
    {
        var memoryLine = File.ReadLines("/proc/meminfo").FirstOrDefault(line => line.StartsWith("MemTotal:", StringComparison.Ordinal));
        if (memoryLine is not null && long.TryParse(memoryLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var memoryKiB))
        {
            memoryMiB = memoryKiB / 1024;
        }
    }

    var disks = new List<InstallationDisk>();
    if (OperatingSystem.IsLinux() && Directory.Exists("/sys/block"))
    {
        foreach (var directory in Directory.EnumerateDirectories("/sys/block").OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal) || name.StartsWith("sr", StringComparison.Ordinal))
            {
                continue;
            }

            var sectors = ReadLong(Path.Combine(directory, "size"));
            var readOnly = ReadLong(Path.Combine(directory, "ro")) == 1;
            var removable = ReadLong(Path.Combine(directory, "removable")) == 1;
            var modelPath = Path.Combine(directory, "device", "model");
            var model = File.Exists(modelPath) ? File.ReadAllText(modelPath).Trim() : name;
            disks.Add(new InstallationDisk($"/dev/{name}", sectors * 512, readOnly, removable, model));
        }
    }

    return new InstallationProbe(
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        memoryMiB,
        Directory.Exists("/sys/firmware/efi"),
        disks);
}

static long ReadLong(string path) =>
    File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : 0;
