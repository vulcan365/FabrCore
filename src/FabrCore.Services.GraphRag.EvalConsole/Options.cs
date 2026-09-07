namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed record Options(string Command, Dictionary<string, string> Values)
{
    public string Value(string key, string fallback) => Values.GetValueOrDefault(key, fallback);
    public int Number(string key, int fallback, int min, int max)
        => int.TryParse(Value(key, fallback.ToString()), out var value) && value >= min && value <= max
            ? value : throw new ArgumentException($"--{key} must be between {min} and {max}.");

    public static Options Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "run";
        if (command is not ("run" or "compare" or "init" or "fetch" or "trace")) throw new ArgumentException($"Unknown command '{command}'.");
        var values = new Dictionary<string, string>();
        var allowed = new[] { "mode", "model", "models", "manifest", "max-documents", "max-chars", "iterations", "output", "cache", "response", "description-chars", "relations", "evidence", "repair", "result-cache", "reingest", "embedding-cache", "taxonomy-cache", "sections-per-batch", "endpoint-aliases", "input", "document-concurrency", "taxonomy-names" };
        for (var i = command == args.FirstOrDefault() ? 1 : 0; i < args.Length; i += 2)
        {
            var key = args[i].StartsWith("--") ? args[i][2..] : "";
            if (!allowed.Contains(key) || i + 1 >= args.Length || args[i + 1].StartsWith("--"))
                throw new ArgumentException($"Invalid option '{args[i]}'; use --help.");
            if (!values.TryAdd(key, args[i + 1])) throw new ArgumentException($"Duplicate option --{key}.");
        }
        if (values.TryGetValue("endpoint-aliases", out var aliases) && aliases is not ("off" or "on"))
            throw new ArgumentException("--endpoint-aliases must be off or on.");
        if (values.TryGetValue("mode", out var mode) && mode is not ("document" or "legacy" or "vector"))
            throw new ArgumentException("--mode must be document, legacy, or vector.");
        if (values.TryGetValue("response", out var response) && response is not ("prompt" or "schema"))
            throw new ArgumentException("--response must be prompt or schema.");
        if (values.TryGetValue("relations", out var relations) && relations is not ("current" or "defined" or "policy" or "policy-obligation"))
            throw new ArgumentException("--relations must be current, defined, policy, or policy-obligation.");
        if (values.TryGetValue("evidence", out var evidence) && evidence is not ("off" or "strict" or "spans"))
            throw new ArgumentException("--evidence must be off, strict, or spans.");
        if (values.TryGetValue("repair", out var repair) && repair is not ("off" or "once"))
            throw new ArgumentException("--repair must be off or once.");
        if (values.TryGetValue("taxonomy-cache", out var taxonomyCache) && taxonomyCache is not ("off" or "on"))
            throw new ArgumentException("--taxonomy-cache must be off or on.");
        if (taxonomyCache == "on" && values.GetValueOrDefault("result-cache", "off") != "on")
            throw new ArgumentException("--taxonomy-cache on requires --result-cache on.");
        if (values.TryGetValue("embedding-cache", out var embeddingCache) && embeddingCache is not ("off" or "on"))
            throw new ArgumentException("--embedding-cache must be off or on.");
        if (values.TryGetValue("result-cache", out var resultCache) && resultCache is not ("off" or "on"))
            throw new ArgumentException("--result-cache must be off or on.");
        if (values.TryGetValue("reingest", out var reingest) && reingest is not ("fresh" or "repeat" or "edit"))
            throw new ArgumentException("--reingest must be fresh, repeat, or edit.");
        if (command == "compare" && values.GetValueOrDefault("reingest", "fresh") != "fresh")
            throw new ArgumentException("--reingest requires run.");
        if (values.GetValueOrDefault("taxonomy-names", "current") is not ("current" or "structured"))
            throw new ArgumentException("--taxonomy-names must be current or structured.");
        if (relations is "policy" or "policy-obligation" && values.GetValueOrDefault("response", "prompt") != "schema")
            throw new ArgumentException("--relations policy requires --response schema.");
        var result = new Options(command, values);
        if (values.ContainsKey("sections-per-batch"))
        {
            result.Number("sections-per-batch", 8, 1, 256);
            if (command != "run" || result.Value("mode", "document") != "document")
                throw new ArgumentException("--sections-per-batch requires run --mode document.");
        }
        var concurrency = result.Number("document-concurrency", 1, 1, 16);
        if (concurrency > 1 && (result.Value("result-cache", "off") != "off" || result.Value("embedding-cache", "off") != "off"))
            throw new ArgumentException("Concurrent document experiments require caches off for accurate per-document cache accounting.");
        return result;
    }
}
