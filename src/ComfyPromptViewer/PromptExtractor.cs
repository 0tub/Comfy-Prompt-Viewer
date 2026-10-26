using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ComfyPromptViewer;

// Turns one file's raw text metadata into its prompt, negative prompt, and generation settings. Sources in priority
// order: Draw Things XMP, the ComfyUI prompt graph, the ComfyUI UI workflow, A1111-style `parameters` infotext, and
// finally a bare prompt field. Each fills only what an earlier one left empty.
public static class PromptExtractor
{
    private const int MaxLinkDepth = 32;
    private static readonly string[] ModelInputKeys = ["ckpt_name", "unet_name", "model_name", "checkpoint", "model"];
    private static readonly string[] NegativeMarkers = ["negative", "neg_prompt", "negative_prompt"];
    private static readonly string[] SamplerTypes = ["KSampler", "KSamplerAdvanced", "SamplerCustom", "SamplerCustomAdvanced"];
    // No "cfg": CFGGuider carries it along with positive/negative links, and is not a sampler.
    private static readonly string[] SamplerSettingKeys = ["sampler_name", "steps", "seed", "noise_seed"];
    private static readonly string[] ModelFileExtensions = [".safetensors", ".ckpt", ".pth", ".pt", ".bin"];
    // Where generators leave a bare prompt: Easy Diffusion (plain-text "prompt"), NovelAI ("Description"), and
    // JPEG/WebP EXIF comments. Every other text field belongs to editors and screenshot tools (Software, Author,
    // dates, XMP), and taking the longest of those showed them as the prompt.
    private static readonly string[] FallbackPromptKeys = ["prompt", "Description", "UserComment"];
    private static readonly Regex LoraStrengthSuffix = new(@"\s\(-?\d+(\.\d+)?\)$", RegexOptions.Compiled);
    private static readonly Regex PromptEmbedding = new(@"embedding:([\p{L}\p{Nd}_.\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Found Nothing = new("", "", null);

    private readonly record struct Found(string Prompt, string NegativePrompt, GenerationSettings? Settings);

    public static ExtractedPromptMetadata ExtractAll(Dictionary<string, string> metadata)
    {
        var result = new ExtractedPromptMetadata();
        if (metadata is null || metadata.Count == 0)
        {
            return result;
        }

        Fill(result, ReadDrawThings(metadata), merge: false);
        if (metadata.TryGetValue("prompt", out var promptJson))
        {
            Fill(result, ReadComfyPrompt(promptJson), merge: false);
        }

        if (metadata.TryGetValue("workflow", out var workflowJson))
        {
            Fill(result, ReadComfyWorkflow(workflowJson), merge: true);
        }

        if (metadata.TryGetValue("parameters", out var parameters))
        {
            Fill(result, ReadInfotext(parameters), merge: false);
        }

        if (IsBlank(result.Prompt))
        {
            result.Prompt = LongestPromptLike(FallbackPromptKeys.Select(key => metadata.GetValueOrDefault(key)).OfType<string>());
        }

        var settings = result.GenerationSettings;
        AddPromptResources(settings, result.Prompt);
        AddPromptResources(settings, result.NegativePrompt);
        // Easy Diffusion also writes a "prompt" key, but as plain text.
        if (IsBlank(settings.Tool) && (IsJsonValue(metadata, "prompt") || IsJsonValue(metadata, "workflow")))
        {
            settings.Tool = "ComfyUI";
        }

        return result;
    }

    // Reverses the "name (0.80), other, third (1.00)" form AddLora produces. The strength suffix is dropped from the
    // name so a search finds the LoRA at every strength.
    internal static List<(string Display, string Name)> SplitLoraEntries(string lora) =>
        lora.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => LoraStrengthSuffix.Match(part) is { Success: true } match ? (part, part[..match.Index]) : (part, part))
            .ToList();

    // The workflow only adds to what the prompt graph found. Other sources' settings are all-or-nothing: their
    // summaries overlap, and merging would list the same entries twice.
    private static void Fill(ExtractedPromptMetadata result, Found found, bool merge)
    {
        if (IsBlank(result.Prompt))
        {
            result.Prompt = found.Prompt;
        }

        if (IsBlank(result.NegativePrompt))
        {
            result.NegativePrompt = found.NegativePrompt;
        }

        if (found.Settings is not { IsEmpty: false } settings)
        {
            return;
        }

        if (!merge)
        {
            if (result.GenerationSettings.IsEmpty)
            {
                result.GenerationSettings = settings;
            }

            return;
        }

        var current = result.GenerationSettings;
        current.Tool = FirstNonBlank(current.Tool, settings.Tool);
        current.Model = FirstNonBlank(current.Model, settings.Model);
        current.Sampler = FirstNonBlank(current.Sampler, settings.Sampler);
        current.Seed = FirstNonBlank(current.Seed, settings.Seed);
        current.Settings = CombineMetadataStrings(current.Settings, settings.Settings);
        current.Lora = CombineMetadataStrings(current.Lora, settings.Lora);
        current.Resources = CombineMetadataStrings(current.Resources, settings.Resources);
    }

    // ComfyUI API prompt: node id -> { class_type, inputs, _meta }, where an input is a value or a [node id, output] link.
    private static Found ReadComfyPrompt(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Nothing;
            }

            var nodes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                nodes[property.Name] = property.Value;
            }

            var prompt = "";
            var negative = "";
            GenerationSettings? settings = null;
            foreach (var sampler in nodes.Values.Where(IsSampler))
            {
                if (IsBlank(prompt))
                {
                    prompt = FollowText(nodes, ConditioningStart(nodes, sampler, "positive"), "positive");
                }

                if (IsBlank(negative))
                {
                    negative = FollowText(nodes, ConditioningStart(nodes, sampler, "negative"), "negative");
                }

                settings ??= ReadSamplerSettings(nodes, sampler);
            }

            // Graphs without a followable sampler: the best text on each side, split by negative markers.
            if (IsBlank(prompt))
            {
                prompt = LongestPromptLike(nodes.Values.Where(node => !IsSampler(node) && !LooksNegative(node)).Select(NodeText));
            }

            if (IsBlank(negative))
            {
                negative = LongestPromptLike(nodes.Values.Where(node => !IsSampler(node) && LooksNegative(node)).Select(NodeText));
            }

            // Refine passes often wire positive and negative to the same conditioning; that is no negative prompt.
            if (negative == prompt)
            {
                negative = "";
            }

            var loras = new List<string>();
            foreach (var node in nodes.Values)
            {
                AddComfyNodeLoras(node, loras);
            }

            if (loras.Count > 0)
            {
                settings ??= new GenerationSettings();
                settings.Lora = CombineMetadataStrings(settings.Lora, string.Join(", ", loras));
            }

            return new Found(prompt, negative, settings);
        }
        catch (JsonException)
        {
            return Nothing;
        }
    }

    // Core samplers by name; others by shape so custom and third-party samplers work: a conditioning or guider link,
    // plus a latent input or sampling settings. ControlNet and other conditioning nodes have the links but neither of the rest.
    private static bool IsSampler(JsonElement node)
    {
        var inputs = Child(node, "inputs");
        return SamplerTypes.Contains(Str(node, "class_type"), StringComparer.OrdinalIgnoreCase) ||
               (LinkedId(Child(inputs, "positive")) is not null || LinkedId(Child(inputs, "guider")) is not null) &&
               (Child(inputs, "latent_image").ValueKind != JsonValueKind.Undefined ||
                SamplerSettingKeys.Any(key => Scalar(Child(inputs, key)) is not null));
    }

    // SamplerCustomAdvanced takes its conditioning from a guider node: BasicGuider holds only the positive side,
    // CFGGuider both.
    private static JsonElement? ConditioningStart(Dictionary<string, JsonElement> nodes, JsonElement sampler, string role)
    {
        var inputs = Child(sampler, "inputs");
        if (Linked(nodes, inputs, role) is { } direct)
        {
            return direct;
        }

        if (Linked(nodes, inputs, "guider") is not { } guider)
        {
            return null;
        }

        return role == "positive" ? guider : Linked(nodes, Child(guider, "inputs"), role);
    }

    // Walks back from a sampler to the node holding the text: through ControlNet, guidance, and combine nodes,
    // and from a text encoder into the string node that feeds it.
    private static string FollowText(Dictionary<string, JsonElement> nodes, JsonElement? start, string role)
    {
        var node = start;
        for (var depth = 0; node is { } current && depth < MaxLinkDepth; depth++)
        {
            // Flux and SD3 negatives are often a zeroed copy of the positive encoder.
            if (Str(current, "class_type").Contains("ZeroOut", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            var inputs = Child(current, "inputs");
            foreach (var key in new[] { role, "prompt", "text" })
            {
                if (Child(inputs, key) is { ValueKind: JsonValueKind.String } text)
                {
                    return CleanCandidate(text.GetString() ?? "");
                }
            }

            node = Linked(nodes, inputs, role) ?? Linked(nodes, inputs, "text") ?? Linked(nodes, inputs, "prompt") ??
                   ConditioningInput(nodes, inputs);
            if (node is null)
            {
                return LongestPromptLike(StringInputs(inputs));
            }
        }

        return "";
    }

    private static JsonElement? ConditioningInput(Dictionary<string, JsonElement> nodes, JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var input in inputs.EnumerateObject())
        {
            if (input.Name.Contains("conditioning", StringComparison.OrdinalIgnoreCase) &&
                LinkedId(input.Value) is { } id && nodes.TryGetValue(id, out var linked))
            {
                return linked;
            }
        }

        return null;
    }

    // Settings sit on the sampler itself (KSampler) or on the nodes feeding it (SamplerCustomAdvanced takes its
    // seed, sampler, and schedule from separate nodes), so the nearest upstream value of each wins.
    private static GenerationSettings? ReadSamplerSettings(Dictionary<string, JsonElement> nodes, JsonElement sampler)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>();
        var queue = new Queue<JsonElement>([sampler]);
        while (queue.TryDequeue(out var node))
        {
            if (Child(node, "inputs") is not { ValueKind: JsonValueKind.Object } inputs)
            {
                continue;
            }

            foreach (var input in inputs.EnumerateObject())
            {
                if (LinkedId(input.Value) is { } id)
                {
                    if (nodes.TryGetValue(id, out var linked))
                    {
                        // A setting fed by a primitive node (steps <- PrimitiveInt) takes that node's value.
                        if (Scalar(Child(Child(linked, "inputs"), "value")) is { Length: > 0 } primitive)
                        {
                            values.TryAdd(input.Name, primitive);
                        }

                        if (visited.Add(id))
                        {
                            queue.Enqueue(linked);
                        }
                    }
                }
                else if (Scalar(input.Value) is { Length: > 0 } value)
                {
                    values.TryAdd(input.Name, value);
                }
            }
        }

        var settings = new GenerationSettings
        {
            Model = FindModel(nodes, sampler),
            Sampler = values.GetValueOrDefault("sampler_name") ?? "",
            Seed = values.GetValueOrDefault("seed") ?? values.GetValueOrDefault("noise_seed") ?? "",
            Settings = JoinNonBlank(
                FormatSetting("Steps", values.GetValueOrDefault("steps")),
                FormatSetting("CFG", values.GetValueOrDefault("cfg")),
                FormatSetting("Scheduler", values.GetValueOrDefault("scheduler")),
                FormatSetting("Denoise", values.GetValueOrDefault("denoise")))
        };
        return settings.IsEmpty ? null : settings;
    }

    // Follows only model links, so an upscale model loader elsewhere in the graph is never taken for the checkpoint.
    private static string FindModel(Dictionary<string, JsonElement> nodes, JsonElement sampler)
    {
        var node = sampler;
        for (var depth = 0; depth < MaxLinkDepth; depth++)
        {
            var inputs = Child(node, "inputs");
            foreach (var key in ModelInputKeys)
            {
                if (Child(inputs, key) is { ValueKind: JsonValueKind.String } name)
                {
                    return CleanCandidate(name.GetString() ?? "");
                }
            }

            if ((Linked(nodes, inputs, "model") ?? Linked(nodes, inputs, "guider")) is not { } next)
            {
                break;
            }

            node = next;
        }

        return "";
    }

    private static string NodeText(JsonElement node)
    {
        var inputs = Child(node, "inputs");
        foreach (var key in new[] { "positive", "prompt", "text" })
        {
            if (Child(inputs, key) is { ValueKind: JsonValueKind.String } text)
            {
                return CleanCandidate(text.GetString() ?? "");
            }
        }

        return LongestPromptLike(StringInputs(inputs));
    }

    // Structural signals only (type, titles, input names), never string values: "negative space" in a positive
    // prompt once classified its node as negative.
    private static bool LooksNegative(JsonElement node)
    {
        var labels = new[]
        {
            Str(node, "class_type"), Str(node, "type"), Str(node, "title"),
            Str(Child(node, "_meta"), "title"), Str(Child(node, "properties"), "Node name for S&R")
        };
        var inputs = Child(node, "inputs");
        var inputNames = inputs.ValueKind == JsonValueKind.Object ? inputs.EnumerateObject().Select(input => input.Name) : [];
        return labels.Concat(inputNames).Any(label => NegativeMarkers.Any(marker => label.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AddComfyNodeLoras(JsonElement node, List<string> loras)
    {
        if (!Str(node, "class_type").Contains("lora", StringComparison.OrdinalIgnoreCase) &&
            !Str(Child(node, "_meta"), "title").Contains("lora", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var inputs = Child(node, "inputs");
        if (Str(inputs, "lora_data") is { Length: > 0 } loraData)
        {
            try
            {
                using var document = JsonDocument.Parse(loraData);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        if (Child(item, "enabled").ValueKind != JsonValueKind.False)
                        {
                            AddLora(loras, Str(item, "name"), Scalar(Child(item, "strength")));
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        AddLora(loras, Str(inputs, "lora_name"), Scalar(Child(inputs, "strength_model")));
    }

    // The UI workflow has no resolved links, so it only yields guesses: the longest widget text on each side, split
    // by negative markers, and the LoRA loaders that are not bypassed.
    private static Found ReadComfyWorkflow(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (Child(document.RootElement, "nodes") is not { ValueKind: JsonValueKind.Array } nodes)
            {
                return Nothing;
            }

            var positive = new List<string>();
            var negative = new List<string>();
            var loras = new List<string>();
            foreach (var node in nodes.EnumerateArray())
            {
                AddWorkflowNodeLoras(node, loras);
                if (!SamplerTypes.Contains(Str(node, "type"), StringComparer.OrdinalIgnoreCase))
                {
                    (LooksNegative(node) ? negative : positive).AddRange(WidgetStrings(node));
                }
            }

            var settings = loras.Count == 0 ? null : new GenerationSettings { Lora = string.Join(", ", loras) };
            return new Found(LongestPromptLike(positive), LongestPromptLike(negative), settings);
        }
        catch (JsonException)
        {
            return Nothing;
        }
    }

    // Widget values only. Node properties hold type names and, in current ComfyUI, "cnr_id": "comfy-core" on every
    // node, which the longest-text guess took for a prompt.
    private static IEnumerable<string> WidgetStrings(JsonElement node) =>
        Child(node, "widgets_values") is { ValueKind: JsonValueKind.Array } widgets
            ? widgets.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString() ?? "")
            : [];

    private static void AddWorkflowNodeLoras(JsonElement node, List<string> loras)
    {
        const int bypassedMode = 4;
        var title = FirstNonBlank(Str(node, "title"), Str(Child(node, "properties"), "Node name for S&R"));
        if (Child(node, "mode") is { ValueKind: JsonValueKind.Number } mode && mode.TryGetInt32(out var modeValue) && modeValue == bypassedMode ||
            !Str(node, "type").Contains("lora", StringComparison.OrdinalIgnoreCase) && !title.Contains("lora", StringComparison.OrdinalIgnoreCase) ||
            Child(node, "widgets_values") is not { ValueKind: JsonValueKind.Array } widgets)
        {
            return;
        }

        // Widgets are positional: the LoRA file name, then its model strength.
        var values = widgets.EnumerateArray().ToList();
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].ValueKind == JsonValueKind.String && values[index].GetString() is { } name &&
                (name.Contains("lora", StringComparison.OrdinalIgnoreCase) || IsModelFileName(name)))
            {
                var strength = index + 1 < values.Count && values[index + 1].ValueKind == JsonValueKind.Number ? values[index + 1].GetRawText() : null;
                AddLora(loras, name, strength);
            }
        }
    }

    // A1111/Forge infotext: the prompt, an optional "Negative prompt:" section, then one settings line.
    private static Found ReadInfotext(string parameters)
    {
        var text = CleanCandidate(parameters);
        var lastLineStart = text.LastIndexOf('\n') + 1;
        var settingsStart = lastLineStart > 0 ? lastLineStart : text.IndexOf("Steps:", StringComparison.OrdinalIgnoreCase);
        var body = settingsStart >= 0 && text[settingsStart..].Contains("Steps:", StringComparison.OrdinalIgnoreCase)
            ? text[..settingsStart]
            : text;

        const string negativeMarker = "Negative prompt:";
        var negativeStart = body.IndexOf(negativeMarker, StringComparison.OrdinalIgnoreCase);
        return negativeStart < 0
            ? new Found(CleanCandidate(body), "", ReadInfotextSettings(text))
            : new Found(CleanCandidate(body[..negativeStart]), CleanCandidate(body[(negativeStart + negativeMarker.Length)..]), ReadInfotextSettings(text));
    }

    private static GenerationSettings? ReadInfotextSettings(string parameters)
    {
        var values = ParseSettingsLine(parameters[(parameters.LastIndexOf('\n') + 1)..]);
        if (values.Count == 0)
        {
            return null;
        }

        var loras = new List<string>();
        var hypernetworks = new List<string>();
        var embeddings = new List<string>();
        var controlNets = new List<string>();
        var ipAdapters = new List<string>();
        foreach (var (key, value) in values)
        {
            if (key.StartsWith("LoRA Model", StringComparison.OrdinalIgnoreCase))
            {
                AddLora(loras, value, values.GetValueOrDefault("LoRA Weight" + key["LoRA Model".Length..]));
            }
            else if (key.StartsWith("AddNet Model", StringComparison.OrdinalIgnoreCase))
            {
                AddResource(loras, value.Split('(')[0]);
            }
            else if (key.Equals("Lora hashes", StringComparison.OrdinalIgnoreCase))
            {
                AddHashListNames(loras, value, validate: false);
            }
            else if (key.Equals("Hypernet", StringComparison.OrdinalIgnoreCase) || key.Equals("Hypernetwork", StringComparison.OrdinalIgnoreCase))
            {
                AddResource(hypernetworks, value.Split('(')[0]);
            }
            else if (key.Equals("TI hashes", StringComparison.OrdinalIgnoreCase))
            {
                AddHashListNames(embeddings, value, validate: true);
            }
            else if (key.StartsWith("ControlNet", StringComparison.OrdinalIgnoreCase) && ControlNetModel(value) is { Length: > 0 } model)
            {
                var isIpAdapter = new[] { "ip-adapter", "ip_adapter", "ipadapter" }.Any(marker => model.Contains(marker, StringComparison.OrdinalIgnoreCase));
                AddResource(isIpAdapter ? ipAdapters : controlNets, model);
            }
        }

        string Get(params string[] keys) =>
            keys.Select(key => values.GetValueOrDefault(key)).FirstOrDefault(value => !IsBlank(value)) ?? "";

        var variationSeed = Get("Variation seed");
        var variationStrength = Get("Variation seed strength");
        return new GenerationSettings
        {
            Tool = DetectTool($"{Get("App")} {Get("Version")}"),
            Model = Get("Model", "Checkpoint", "Model hash"),
            Sampler = Get("Sampler"),
            Seed = Get("Seed"),
            Settings = JoinNonBlank(
                FormatSetting("Steps", Get("Steps")),
                FormatSetting("CFG", Get("CFG scale", "CFG", "Guidance Scale")),
                FormatSetting("Scheduler", Get("Schedule type", "Scheduler")),
                FormatSetting("Denoise", Get("Denoising strength", "Denoise", "Strength")),
                FormatSetting("Clip skip", Get("Clip skip")),
                FormatSetting("VAE", Get("VAE")),
                FormatSetting("Hires", JoinNonBlank(Get("Hires upscale"), FormatSetting("steps", Get("Hires steps")), Get("Hires upscaler"))),
                FormatSetting("Hash", Get("Model hash", "sd_model_hash")),
                FormatSetting("Variation", IsBlank(variationSeed) || IsBlank(variationStrength) ? variationSeed : $"{variationSeed}:{variationStrength}")),
            Lora = string.Join(", ", loras),
            Resources = JoinNonBlank(
                ResourceGroup("Hypernet", hypernetworks),
                ResourceGroup("Embedding", embeddings),
                ResourceGroup("ControlNet", controlNets),
                ResourceGroup("IP-Adapter", ipAdapters))
        };
    }

    private static string DetectTool(string appAndVersion)
    {
        var text = appAndVersion.ToLowerInvariant();
        return text.Contains("sd.next") || text.Contains("sdnext") || text.Contains("vlad") ? "SD.Next"
            : text.Contains("forge") ? "Forge"
            : text.Contains("anapnoe") ? "Anapnoe"
            : text.Contains("comfy") ? "ComfyUI"
            : "";
    }

    private static Dictionary<string, string> ParseSettingsLine(string settingsLine)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitSettingParts(settingsLine))
        {
            var separator = part.IndexOf(':');
            if (separator > 0 && separator < part.Length - 1)
            {
                var key = part[..separator].Trim();
                var value = part[(separator + 1)..].Trim().Trim('"');
                if (key.Length > 0 && value.Length > 0)
                {
                    values[key] = value;
                }
            }
        }

        return values;
    }

    // Splits on commas outside quotes and brackets, so "ControlNet 0: "Module: x, Model: y"" stays one part.
    private static List<string> SplitSettingParts(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var inQuotes = false;
        var depth = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var c = index < text.Length ? text[index] : ',';
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (!inQuotes && c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
            }
            else if (c == ',' && (index == text.Length || !inQuotes && depth == 0))
            {
                if (text[start..index].Trim() is { Length: > 0 } part)
                {
                    parts.Add(part);
                }

                start = index + 1;
            }
        }

        return parts;
    }

    // "name: hash" pairs. Embedding names are validated because TI hash lists also carry prompt fragments.
    private static void AddHashListNames(List<string> target, string value, bool validate)
    {
        foreach (var part in SplitSettingParts(value))
        {
            if (part.Split(':', 2) is not [var name, var hash])
            {
                continue;
            }

            name = CleanResourceName(name);
            hash = hash.Trim();
            var valid = !validate ||
                        name.Length is > 0 and < 100 && name.IndexOfAny(['(', ')']) < 0 && !name.EndsWith('+') && !name.EndsWith('-') &&
                        hash.Length is >= 8 and <= 128 && hash.All(char.IsAsciiLetterOrDigit);
            if (valid)
            {
                AddDistinct(target, name);
            }
        }
    }

    private static string ControlNetModel(string value)
    {
        const string marker = "Model: ";
        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        // "name [hash]": the hash is dropped.
        var model = value[(start + marker.Length)..].Split(',')[0].Trim().Trim('"');
        var hashStart = model.LastIndexOf('[');
        if (hashStart > 0 && model.LastIndexOf(']') > hashStart)
        {
            model = model[..hashStart];
        }

        return CleanResourceName(model);
    }

    // Draw Things writes an XMP description: prompt lines, "-"-prefixed negative lines, then a settings line.
    private static Found ReadDrawThings(Dictionary<string, string> metadata)
    {
        var xml = metadata.Values.FirstOrDefault(value =>
            value.Contains("Draw Things", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("<dc:description>", StringComparison.OrdinalIgnoreCase));
        if (xml is null)
        {
            return Nothing;
        }

        try
        {
            var items = XDocument.Parse(xml).Descendants().Where(element => element.Name.LocalName == "li").ToList();
            var description = items.FirstOrDefault(element => element.Parent?.Parent?.Name.LocalName == "description") ?? items.FirstOrDefault();
            if (description is null || IsBlank(description.Value))
            {
                return Nothing;
            }

            var text = CleanCandidate(description.Value);
            var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
            var settingsIndex = lines.FindIndex(line =>
                line.Contains("Steps:", StringComparison.OrdinalIgnoreCase) && line.Contains("Seed:", StringComparison.OrdinalIgnoreCase));
            if (settingsIndex < 0)
            {
                return new Found(text, "", new GenerationSettings { Tool = "Draw Things" });
            }

            var body = lines.Take(settingsIndex).ToList();
            var settings = ReadInfotextSettings(lines[settingsIndex]) ?? new GenerationSettings();
            settings.Tool = "Draw Things";
            return new Found(
                string.Join("\n", body.Where(line => !line.StartsWith('-'))),
                string.Join("\n", body.Where(line => line.StartsWith('-')).Select(line => line[1..].Trim())),
                settings);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to parse Draw Things XMP metadata: {ex.Message}");
            return Nothing;
        }
    }

    // <lora:name:weight>, <hypernet:name:weight>, and embedding:name written into the prompt text itself.
    private static void AddPromptResources(GenerationSettings settings, string prompt)
    {
        if (IsBlank(prompt))
        {
            return;
        }

        var loras = settings.Lora.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        AddTagResources(prompt, "lora", loras);
        settings.Lora = string.Join(", ", loras);

        var hypernets = new List<string>();
        AddTagResources(prompt, "hypernet", hypernets);
        var embeddings = new List<string>();
        foreach (Match match in PromptEmbedding.Matches(prompt))
        {
            AddResource(embeddings, match.Groups[1].Value);
        }

        settings.Resources = CombineMetadataStrings(settings.Resources,
            JoinNonBlank(ResourceGroup("Hypernet", hypernets), ResourceGroup("Embedding", embeddings)));
    }

    private static void AddTagResources(string prompt, string tag, List<string> target)
    {
        foreach (Match match in Regex.Matches(prompt, $"<{tag}:([^>]*)>", RegexOptions.IgnoreCase))
        {
            var pieces = match.Groups[1].Value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length > 0)
            {
                AddLora(target, pieces[0], pieces.Length > 1 ? pieces[1] : null);
            }
        }
    }

    private static void AddLora(List<string> loras, string name, string? strength)
    {
        name = CleanResourceName(name);
        if (name.Length == 0)
        {
            return;
        }

        if (float.TryParse(strength, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
        {
            name = $"{name} ({weight.ToString("0.00", CultureInfo.InvariantCulture)})";
        }

        AddDistinct(loras, name);
    }

    private static void AddResource(List<string> target, string name) => AddDistinct(target, CleanResourceName(name));

    private static void AddDistinct(List<string> target, string value)
    {
        if (value.Length > 0 && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }
    }

    // Normalized so the same resource matches however a tool spelled it: no folder, no extension, lower snake case.
    private static string CleanResourceName(string value)
    {
        value = CleanCandidate(value).Trim('"');
        value = value[(value.LastIndexOfAny(['/', '\\']) + 1)..];
        if (ModelFileExtensions.FirstOrDefault(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) is { } extension)
        {
            value = value[..^extension.Length];
        }

        return value.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
    }

    private static string ResourceGroup(string label, List<string> values) =>
        values.Count == 0 ? "" : $"{label}: {string.Join(", ", values)}";

    private static string FormatSetting(string label, string? value) =>
        IsBlank(value) ? "" : $"{label} {CleanCandidate(value!)}";

    private static string JoinNonBlank(params string[] parts) => string.Join(", ", parts.Where(part => !IsBlank(part)));

    private static string CombineMetadataStrings(string first, string second) =>
        IsBlank(first) ? second
        : IsBlank(second) || first.Contains(second, StringComparison.OrdinalIgnoreCase) ? first
        : $"{first}, {second}";

    private static string LongestPromptLike(IEnumerable<string> values) =>
        values.Select(CleanCandidate).Where(IsPromptLike).MaxBy(value => value.Length) ?? "";

    // JSON graphs, XMP/XML packets, and model file names are data, not prompts.
    private static bool IsPromptLike(string value) =>
        value.Length >= 3 && value[0] is not ('{' or '[' or '<') && !IsModelFileName(value) && value.Any(char.IsLetter);

    private static bool IsModelFileName(string value) =>
        ModelFileExtensions.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string CleanCandidate(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static bool IsJsonValue(Dictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && value.AsSpan().TrimStart() is ['{', ..] or ['[', ..];

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string FirstNonBlank(string current, string extra) => IsBlank(current) ? extra : current;

    // JSON access that treats anything missing or of the wrong kind as absent.
    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) ? child : default;

    private static string Str(JsonElement element, string name) =>
        Child(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

    private static IEnumerable<string> StringInputs(JsonElement inputs) =>
        inputs.ValueKind == JsonValueKind.Object
            ? inputs.EnumerateObject().Where(input => input.Value.ValueKind == JsonValueKind.String).Select(input => input.Value.GetString() ?? "")
            : [];

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => CleanCandidate(value.GetString() ?? ""),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    // A link is exactly [node id, output index]; other arrays are plain list values.
    private static string? LinkedId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2 && value[1].ValueKind == JsonValueKind.Number
            ? value[0].ValueKind switch
            {
                JsonValueKind.String => value[0].GetString(),
                JsonValueKind.Number => value[0].GetRawText(),
                _ => null
            }
            : null;

    private static JsonElement? Linked(Dictionary<string, JsonElement> nodes, JsonElement inputs, string name) =>
        LinkedId(Child(inputs, name)) is { } id && nodes.TryGetValue(id, out var node) ? node : null;
}

public class ExtractedPromptMetadata
{
    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public GenerationSettings GenerationSettings { get; set; } = new();
}

public class GenerationSettings
{
    public string Tool { get; set; } = "";
    public string Model { get; set; } = "";
    public string Sampler { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Settings { get; set; } = "";
    public string Lora { get; set; } = "";
    public string Resources { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Tool) &&
                           string.IsNullOrWhiteSpace(Model) &&
                           string.IsNullOrWhiteSpace(Sampler) &&
                           string.IsNullOrWhiteSpace(Seed) &&
                           string.IsNullOrWhiteSpace(Settings) &&
                           string.IsNullOrWhiteSpace(Lora) &&
                           string.IsNullOrWhiteSpace(Resources);
}
