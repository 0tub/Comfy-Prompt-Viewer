using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ComfyPromptViewer;

public static class PromptExtractor
{
    private static readonly string[] PositiveKeys = ["positive", "prompt", "text"];
    private static readonly string[] ModelInputKeys = ["ckpt_name", "unet_name", "model_name", "checkpoint", "model"];
    private static readonly string[] NegativeMarkers = ["negative", "neg_prompt", "negative_prompt"];
    private const int MaxModelLinkDepth = 6;

    public static ExtractedPromptMetadata ExtractAll(Dictionary<string, string> metadata)
    {
        if (metadata == null || metadata.Count == 0)
        {
            return new ExtractedPromptMetadata();
        }

        var prompt = "";
        var negativePrompt = "";
        var generationSettings = new GenerationSettings();
        var hasComfyMetadata = metadata.ContainsKey("prompt") || metadata.ContainsKey("workflow");

        var xmpText = FindDrawThingsXmp(metadata);
        var xmp = xmpText is null ? null : ParseDrawThingsXmp(xmpText);
        if (xmp is not null)
        {
            prompt = xmp.Prompt;
            negativePrompt = xmp.NegativePrompt;
            if (!string.IsNullOrWhiteSpace(xmp.SettingsLine))
            {
                generationSettings = ExtractGenerationFromParameters(xmp.SettingsLine) ?? generationSettings;
            }
            generationSettings.Tool = "Draw Things";
        }

        if (metadata.TryGetValue("prompt", out var promptJson))
        {
            var comfy = ExtractComfyPromptJson(promptJson);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = comfy.Prompt;
            }
            if (string.IsNullOrWhiteSpace(negativePrompt))
            {
                negativePrompt = comfy.NegativePrompt;
            }
            if (generationSettings.IsEmpty && comfy.GenerationSettings is { IsEmpty: false } comfySettings)
            {
                generationSettings = comfySettings;
            }
        }

        if (metadata.TryGetValue("workflow", out var workflowJson))
        {
            var workflow = ExtractWorkflowJson(workflowJson);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = workflow.Prompt;
            }
            if (string.IsNullOrWhiteSpace(negativePrompt))
            {
                negativePrompt = workflow.NegativePrompt;
            }
            if (workflow.GenerationSettings is { IsEmpty: false } workflowSettings)
            {
                generationSettings = MergeGenerationSettings(generationSettings, workflowSettings);
            }
        }

        if (metadata.TryGetValue("parameters", out var parameters))
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = ExtractFromParameters(parameters);
            }
            if (string.IsNullOrWhiteSpace(negativePrompt))
            {
                negativePrompt = ExtractNegativeFromParameters(parameters);
            }
            if (generationSettings.IsEmpty)
            {
                generationSettings = ExtractGenerationFromParameters(parameters) ?? generationSettings;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = LongestPromptLike(metadata.Values);
        }

        MergePromptResources(generationSettings, prompt);
        MergePromptResources(generationSettings, negativePrompt);
        if (hasComfyMetadata && string.IsNullOrWhiteSpace(generationSettings.Tool))
        {
            generationSettings.Tool = "ComfyUI";
        }

        return new ExtractedPromptMetadata
        {
            Prompt = prompt,
            NegativePrompt = negativePrompt,
            GenerationSettings = generationSettings
        };
    }

    private static ComfyPromptMetadata ExtractComfyPromptJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ComfyPromptMetadata();
            }

            var nodes = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                nodes[property.Name] = property.Value;
            }

            var prompt = "";
            var negativePrompt = "";
            GenerationSettings? generationSettings = null;
            var loras = new List<string>();

            foreach (var node in nodes.Values)
            {
                AddComfyNodeLoras(node, loras);

                if (!IsKSamplerNode(node))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(prompt) &&
                    TryGetLinkedText(nodes, node, "positive", out var linkedPrompt))
                {
                    prompt = linkedPrompt;
                }

                if (string.IsNullOrWhiteSpace(negativePrompt) &&
                    TryGetLinkedText(nodes, node, "negative", out var linkedNegativePrompt))
                {
                    negativePrompt = linkedNegativePrompt;
                }

                generationSettings ??= new GenerationSettings
                {
                    Model = FindModelName(nodes, node) ?? "",
                    Sampler = TryGetInputScalar(node, "sampler_name") ?? "",
                    Seed = TryGetInputScalar(node, "seed") ?? TryGetInputScalar(node, "noise_seed") ?? "",
                    Settings = BuildSettingsSummary([
                        FormatSetting("Steps", TryGetInputScalar(node, "steps")),
                        FormatSetting("CFG", TryGetInputScalar(node, "cfg")),
                        FormatSetting("Scheduler", TryGetInputScalar(node, "scheduler")),
                        FormatSetting("Denoise", TryGetInputScalar(node, "denoise"))
                    ]),
                    Lora = string.Join(", ", loras)
                };
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = FindBestTextInput(nodes.Values);
            }

            if (string.IsNullOrWhiteSpace(negativePrompt))
            {
                var bestNegativePrompt = "";
                foreach (var node in nodes.Values)
                {
                    if (!IsKSamplerNode(node) && NodeLooksNegative(node))
                    {
                        KeepBestPromptLike(ref bestNegativePrompt, ExtractTextFromNode(node));
                    }
                }

                negativePrompt = bestNegativePrompt;
            }

            if (loras.Count > 0)
            {
                generationSettings ??= new GenerationSettings();
                generationSettings.Lora = CombineMetadataStrings(generationSettings.Lora, string.Join(", ", loras));
            }

            return new ComfyPromptMetadata
            {
                Prompt = prompt,
                NegativePrompt = negativePrompt,
                GenerationSettings = generationSettings
            };
        }
        catch (JsonException)
        {
            return new ComfyPromptMetadata();
        }
    }

    private static void AddComfyNodeLoras(JsonElement node, List<string> loras)
    {
        var classType = TryGetStringProperty(node, "class_type");
        var title = "";
        if (node.TryGetProperty("_meta", out var meta) &&
            meta.ValueKind == JsonValueKind.Object)
        {
            title = TryGetStringProperty(meta, "title");
        }

        if (!classType.Contains("lora", StringComparison.OrdinalIgnoreCase) &&
            !title.Contains("lora", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!node.TryGetProperty("inputs", out var inputs) ||
            inputs.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (inputs.TryGetProperty("lora_data", out var loraData) &&
            loraData.ValueKind == JsonValueKind.String)
        {
            AddLoraDataJson(loras, loraData.GetString() ?? "");
        }

        AddLora(
            loras,
            TryGetStringProperty(inputs, "lora_name"),
            TryGetScalarProperty(inputs, "strength_model"));

    }

    private static void AddLoraDataJson(List<string> loras, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("enabled", out var enabled) &&
                    enabled.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                var name = TryGetStringProperty(item, "name");
                var strength = TryGetScalarProperty(item, "strength");
                AddLora(loras, name, strength);
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string TryGetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static string TryGetScalarProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static void AddLora(List<string> loras, string name, string? strength)
    {
        name = CleanResourceName(name);
        if (name.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(strength) &&
            float.TryParse(strength, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
        {
            name = $"{name} ({weight.ToString("0.00", CultureInfo.InvariantCulture)})";
        }

        AddUniqueCleaned(loras, name);
    }

    private static GenerationSettings? ExtractGenerationFromParameters(string parameters)
    {
        var values = ParseParameterSettings(parameters);
        if (values.Count == 0)
        {
            return null;
        }

        var model = GetFirst(values, "Model", "Checkpoint", "Model hash") ?? "";
        var sampler = GetFirst(values, "Sampler") ?? "";
        var seed = GetFirst(values, "Seed") ?? "";
        var tool = DetectTool(values);
        var settings = BuildSettingsSummary([
            FormatSetting("Steps", GetFirst(values, "Steps")),
            FormatSetting("CFG", GetFirst(values, "CFG scale", "CFG", "Guidance Scale")),
            FormatSetting("Scheduler", GetFirst(values, "Schedule type", "Scheduler")),
            FormatSetting("Denoise", GetFirst(values, "Denoising strength", "Denoise", "Strength")),
            FormatSetting("Clip skip", GetFirst(values, "Clip skip")),
            FormatSetting("VAE", GetFirst(values, "VAE")),
            FormatSetting("Hires", BuildHiresSummary(values)),
            FormatSetting("Hash", GetFirst(values, "Model hash", "sd_model_hash")),
            FormatSetting("Variation", BuildVariationSummary(values))
        ]);

        var loras = new List<string>();
        var hypernetworks = new List<string>();
        var embeddings = new List<string>();
        var controlNets = new List<string>();
        var ipAdapters = new List<string>();
        foreach (var entry in values)
        {
            var key = entry.Key;
            if (key.StartsWith("LoRA Model", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = key.Substring("LoRA Model".Length);
                var modelName = entry.Value;
                var weightKey = "LoRA Weight" + suffix;
                if (values.TryGetValue(weightKey, out var weightVal))
                {
                    loras.Add($"{modelName} ({weightVal})");
                }
                else
                {
                    loras.Add(modelName);
                }
            }
            else if (key.StartsWith("AddNet Model", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(loras, CleanResourceName(entry.Value.Split('(')[0]));
            }
            else if (key.Equals("Lora hashes", StringComparison.OrdinalIgnoreCase))
            {
                AddHashListNames(loras, entry.Value);
            }
            else if (key.Equals("Hypernet", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("Hypernetwork", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(hypernetworks, CleanResourceName(entry.Value.Split('(')[0]));
            }
            else if (key.Equals("TI hashes", StringComparison.OrdinalIgnoreCase))
            {
                AddValidatedHashListNames(embeddings, entry.Value);
            }
            else if (key.StartsWith("ControlNet", StringComparison.OrdinalIgnoreCase))
            {
                var controlModel = ExtractControlNetModel(entry.Value);
                if (controlModel.Length > 0)
                {
                    if (LooksLikeIpAdapter(controlModel))
                    {
                        AddUnique(ipAdapters, controlModel);
                    }
                    else
                    {
                        AddUnique(controlNets, controlModel);
                    }
                }
            }
        }
        var lora = string.Join(", ", loras);
        var resources = BuildResourcesSummary([
            FormatResourceGroup("Hypernet", hypernetworks),
            FormatResourceGroup("Embedding", embeddings),
            FormatResourceGroup("ControlNet", controlNets),
            FormatResourceGroup("IP-Adapter", ipAdapters)
        ]);

        return new GenerationSettings
        {
            Tool = tool,
            Model = model,
            Sampler = sampler,
            Seed = seed,
            Settings = settings,
            Lora = lora,
            Resources = resources
        };
    }

    private static WorkflowPromptMetadata ExtractWorkflowJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                var promptCandidates = new List<string>();
                var negativeCandidates = new List<string>();
                var loras = new List<string>();
                foreach (var node in nodes.EnumerateArray())
                {
                    AddWorkflowNodeLoras(node, loras);

                    if (IsWorkflowSamplerNode(node))
                    {
                        continue;
                    }

                    if (NodeLooksNegative(node))
                    {
                        AddWorkflowCandidates(node, negativeCandidates);
                        continue;
                    }

                    AddWorkflowCandidates(node, promptCandidates);
                }

                return new WorkflowPromptMetadata
                {
                    Prompt = LongestPromptLike(promptCandidates),
                    NegativePrompt = LongestPromptLike(negativeCandidates),
                    GenerationSettings = loras.Count == 0 ? null : new GenerationSettings
                    {
                        Lora = string.Join(", ", loras)
                    }
                };
            }
        }
        catch (JsonException)
        {
            return new WorkflowPromptMetadata();
        }

        return new WorkflowPromptMetadata();
    }

    private static GenerationSettings MergeGenerationSettings(GenerationSettings current, GenerationSettings extra)
    {
        current.Tool = FirstNonEmpty(current.Tool, extra.Tool);
        current.Model = FirstNonEmpty(current.Model, extra.Model);
        current.Sampler = FirstNonEmpty(current.Sampler, extra.Sampler);
        current.Seed = FirstNonEmpty(current.Seed, extra.Seed);
        current.Settings = CombineMetadataStrings(current.Settings, extra.Settings);
        current.Lora = CombineMetadataStrings(current.Lora, extra.Lora);
        current.Resources = CombineMetadataStrings(current.Resources, extra.Resources);
        return current;
    }

    private static string FirstNonEmpty(string current, string extra)
    {
        return string.IsNullOrWhiteSpace(current) ? extra : current;
    }

    private static bool TryGetLinkedText(
        Dictionary<string, JsonElement> nodes,
        JsonElement node,
        string inputName,
        out string prompt)
    {
        prompt = "";
        if (!node.TryGetProperty("inputs", out var inputs) ||
            !inputs.TryGetProperty(inputName, out var positiveInput) ||
            positiveInput.ValueKind != JsonValueKind.Array ||
            positiveInput.GetArrayLength() == 0)
        {
            return false;
        }

        var linkedNodeId = positiveInput[0].ValueKind switch
        {
            JsonValueKind.String => positiveInput[0].GetString(),
            JsonValueKind.Number => positiveInput[0].GetRawText(),
            _ => null
        };

        if (linkedNodeId is null || !nodes.TryGetValue(linkedNodeId, out var linkedNode))
        {
            return false;
        }

        prompt = ExtractTextFromNode(linkedNode);
        return !string.IsNullOrWhiteSpace(prompt);
    }

    private static string FindBestTextInput(IEnumerable<JsonElement> nodes)
    {
        var best = "";
        foreach (var node in nodes)
        {
            if (!IsKSamplerNode(node) && !NodeLooksNegative(node))
            {
                KeepBestPromptLike(ref best, ExtractTextFromNode(node));
            }
        }

        return best;
    }

    private static string ExtractTextFromNode(JsonElement node)
    {
        if (!node.TryGetProperty("inputs", out var inputs))
        {
            return "";
        }

        foreach (var key in PositiveKeys)
        {
            if (inputs.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return CleanCandidate(value.GetString() ?? "");
            }
        }

        var best = "";
        foreach (var property in inputs.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                KeepBestPromptLike(ref best, property.Value.GetString() ?? "");
            }
        }

        return best;
    }

    private static void AddWorkflowCandidates(JsonElement node, List<string> candidates)
    {
        if (node.TryGetProperty("widgets_values", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
        {
            foreach (var widget in widgets.EnumerateArray())
            {
                if (widget.ValueKind == JsonValueKind.String)
                {
                    candidates.Add(widget.GetString() ?? "");
                }
            }
        }

        if (node.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    candidates.Add(property.Value.GetString() ?? "");
                }
            }
        }
    }

    private static void AddWorkflowNodeLoras(JsonElement node, List<string> loras)
    {
        if (WorkflowNodeIsDisabled(node))
        {
            return;
        }

        var type = TryGetStringProperty(node, "type");
        var title = TryGetStringProperty(node, "title");
        if (node.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            title = FirstNonEmpty(title, TryGetStringProperty(properties, "Node name for S&R"));
        }

        if (!type.Contains("lora", StringComparison.OrdinalIgnoreCase) &&
            !title.Contains("lora", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!node.TryGetProperty("widgets_values", out var widgets) ||
            widgets.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var values = widgets.EnumerateArray().ToList();
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = values[index].GetString() ?? "";
            if (!LooksLikeLoraFileName(name))
            {
                continue;
            }

            var strength = "";
            if (index + 1 < values.Count && values[index + 1].ValueKind == JsonValueKind.Number)
            {
                strength = values[index + 1].GetRawText();
            }

            AddLora(loras, name, strength);
        }
    }

    private static bool WorkflowNodeIsDisabled(JsonElement node)
    {
        return node.TryGetProperty("mode", out var mode) &&
               mode.ValueKind == JsonValueKind.Number &&
               mode.TryGetInt32(out var modeValue) &&
               modeValue == 4;
    }

    private static bool LooksLikeLoraFileName(string value)
    {
        return value.Contains("lora", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".pth", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractFromParameters(string parameters)
    {
        var index = parameters.IndexOf("Negative prompt:", StringComparison.OrdinalIgnoreCase);
        return CleanCandidate(index > 0 ? parameters[..index] : parameters);
    }



    private static string BuildSettingsSummary(IEnumerable<string> settings)
    {
        var builder = new StringBuilder();
        foreach (var setting in settings)
        {
            if (string.IsNullOrWhiteSpace(setting))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(setting);
        }

        return builder.ToString();
    }

    private static string FormatSetting(string label, string? value)
    {
        value = CleanCandidate(value ?? "");
        return string.IsNullOrWhiteSpace(value) ? "" : $"{label} {value}";
    }

    private static Dictionary<string, string> ParseParameterSettings(string parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lastLineStart = parameters.LastIndexOf('\n');
        var settingsLine = lastLineStart >= 0 ? parameters[(lastLineStart + 1)..] : parameters;

        foreach (var partText in SplitParameterParts(settingsLine))
        {
            var part = partText.AsSpan().Trim();
            var separator = part.IndexOf(':');
            if (separator > 0 && separator < part.Length - 1)
            {
                var key = part[..separator].Trim();
                var value = part[(separator + 1)..].Trim();
                if (!key.IsEmpty && !value.IsEmpty)
                {
                    values[key.ToString()] = value.Trim('"').ToString();
                }
            }
        }

        return values;
    }

    private static List<string> SplitParameterParts(string settingsLine)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;
        var depth = 0;

        foreach (var c in settingsLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                builder.Append(c);
            }
            else if (!inQuotes && (c == '(' || c == '[' || c == '{'))
            {
                depth++;
                builder.Append(c);
            }
            else if (!inQuotes && (c == ')' || c == ']' || c == '}'))
            {
                if (depth > 0)
                {
                    depth--;
                }
                builder.Append(c);
            }
            else if (c == ',' && !inQuotes && depth == 0)
            {
                AddParameterPart(parts, builder);
            }
            else
            {
                builder.Append(c);
            }
        }

        AddParameterPart(parts, builder);
        return parts;
    }

    private static void AddParameterPart(List<string> parts, StringBuilder builder)
    {
        var part = builder.ToString().Trim();
        if (part.Length > 0)
        {
            parts.Add(part);
        }
        builder.Clear();
    }

    private static string? GetFirst(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string DetectTool(Dictionary<string, string> values)
    {
        var text = $"{GetFirst(values, "App") ?? ""} {GetFirst(values, "Version") ?? ""}".ToLowerInvariant();
        if (text.Contains("sd.next") || text.Contains("sdnext") || text.Contains("vlad"))
        {
            return "SD.Next";
        }
        if (text.Contains("forge"))
        {
            return "Forge";
        }
        if (text.Contains("anapnoe"))
        {
            return "Anapnoe";
        }
        if (text.Contains("comfy"))
        {
            return "ComfyUI";
        }

        return "";
    }

    private static string BuildHiresSummary(Dictionary<string, string> values)
    {
        return BuildSettingsSummary([
            GetFirst(values, "Hires upscale") ?? "",
            FormatSetting("steps", GetFirst(values, "Hires steps")),
            GetFirst(values, "Hires upscaler") ?? ""
        ]);
    }

    private static string BuildVariationSummary(Dictionary<string, string> values)
    {
        var seed = GetFirst(values, "Variation seed");
        var strength = GetFirst(values, "Variation seed strength");
        if (string.IsNullOrWhiteSpace(seed))
        {
            return "";
        }

        return string.IsNullOrWhiteSpace(strength) ? seed : $"{seed}:{strength}";
    }

    private static string BuildResourcesSummary(IEnumerable<string> groups)
    {
        return string.Join(", ", groups.Where(group => !string.IsNullOrWhiteSpace(group)));
    }

    private static string FormatResourceGroup(string label, List<string> values)
    {
        return values.Count == 0 ? "" : $"{label}: {string.Join(", ", values)}";
    }

    private static void AddHashListNames(List<string> target, string value)
    {
        foreach (var part in SplitParameterParts(value))
        {
            if (part.Split(':', 2) is [var name, _])
            {
                AddUnique(target, CleanResourceName(name));
            }
        }
    }

    private static void AddValidatedHashListNames(List<string> target, string value)
    {
        foreach (var part in SplitParameterParts(value))
        {
            if (part.Split(':', 2) is not [var name, var hash])
            {
                continue;
            }

            name = CleanResourceName(name);
            hash = hash.Trim();
            var validName = name.Length is > 0 and < 100 &&
                            !name.Contains('(') &&
                            !name.Contains(')') &&
                            !name.EndsWith('+') &&
                            !name.EndsWith('-');
            var validHash = hash.Length is >= 8 and <= 128 && hash.All(char.IsAsciiLetterOrDigit);
            if (validName && validHash)
            {
                AddUnique(target, name);
            }
        }
    }

    private static string ExtractControlNetModel(string value)
    {
        const string marker = "Model: ";
        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        var model = value[(start + marker.Length)..].Split(',')[0].Trim().Trim('"');
        var hashStart = model.LastIndexOf('[');
        var hashEnd = model.LastIndexOf(']');
        if (hashStart > 0 && hashEnd > hashStart)
        {
            model = model[..hashStart].Trim();
        }

        return CleanResourceName(model);
    }

    private static bool LooksLikeIpAdapter(string value)
    {
        return value.Contains("ip-adapter", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ip_adapter", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ipadapter", StringComparison.OrdinalIgnoreCase);
    }

    private static void MergePromptResources(GenerationSettings settings, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var loras = SplitExisting(settings.Lora);
        ExtractTagResources(prompt, "lora", loras, includeWeight: true);
        settings.Lora = string.Join(", ", loras);

        var hypernets = new List<string>();
        ExtractTagResources(prompt, "hypernet", hypernets, includeWeight: true);
        var embeddings = ExtractPromptEmbeddings(prompt);
        var promptResources = BuildResourcesSummary([
            FormatResourceGroup("Hypernet", hypernets),
            FormatResourceGroup("Embedding", embeddings)
        ]);
        settings.Resources = CombineMetadataStrings(settings.Resources, promptResources);
    }

    private static List<string> SplitExisting(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static void ExtractTagResources(string prompt, string tag, List<string> target, bool includeWeight)
    {
        var marker = "<" + tag + ":";
        var start = 0;
        while (start < prompt.Length)
        {
            var index = prompt.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return;
            }

            var end = prompt.IndexOf('>', index + marker.Length);
            if (end < 0)
            {
                return;
            }

            var body = prompt.Substring(index + marker.Length, end - index - marker.Length);
            var pieces = body.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length > 0)
            {
                var name = CleanResourceName(pieces[0]);
                if (includeWeight &&
                    pieces.Length > 1 &&
                    float.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
                {
                    name = $"{name} ({weight.ToString("0.00", CultureInfo.InvariantCulture)})";
                }
                AddUniqueCleaned(target, name);
            }

            start = end + 1;
        }
    }

    private static List<string> ExtractPromptEmbeddings(string prompt)
    {
        var embeddings = new List<string>();
        const string marker = "embedding:";
        var start = 0;
        while (start < prompt.Length)
        {
            var index = prompt.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            var nameStart = index + marker.Length;
            var nameEnd = nameStart;
            while (nameEnd < prompt.Length)
            {
                var c = prompt[nameEnd];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
                {
                    break;
                }
                nameEnd++;
            }

            if (nameEnd > nameStart)
            {
                AddUnique(embeddings, CleanResourceName(prompt[nameStart..nameEnd]));
            }
            start = nameEnd + 1;
        }

        return embeddings;
    }

    private static string CombineMetadataStrings(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }
        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return first.Contains(second, StringComparison.OrdinalIgnoreCase) ? first : $"{first}, {second}";
    }

    private static void AddUnique(List<string> target, string value)
    {
        value = CleanResourceName(value);
        AddUniqueCleaned(target, value);
    }

    private static void AddUniqueCleaned(List<string> target, string value)
    {
        if (value.Length > 0 && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }
    }

    private static string CleanResourceName(string value)
    {
        value = CleanCandidate(value).Trim('"');
        var lastSlash = value.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0 && lastSlash < value.Length - 1)
        {
            value = value[(lastSlash + 1)..];
        }

        foreach (var extension in new[] { ".safetensors", ".ckpt", ".pth", ".pt", ".bin" })
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^extension.Length];
                break;
            }
        }

        return value.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
    }

    private static string? FindModelName(Dictionary<string, JsonElement> nodes, JsonElement node, int depth = 0)
    {
        if (depth > MaxModelLinkDepth || !node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in ModelInputKeys)
        {
            if (inputs.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return CleanCandidate(value.GetString() ?? "");
            }
        }

        if (TryGetLinkedNodeId(inputs, "model", out var linkedNodeId) &&
            nodes.TryGetValue(linkedNodeId, out var linkedNode))
        {
            return FindModelName(nodes, linkedNode, depth + 1);
        }

        return null;
    }

    private static string? TryGetInputScalar(JsonElement node, string inputName)
    {
        if (!node.TryGetProperty("inputs", out var inputs) ||
            !inputs.TryGetProperty(inputName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => CleanCandidate(value.GetString() ?? ""),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool TryGetLinkedNodeId(JsonElement inputs, string inputName, out string linkedNodeId)
    {
        linkedNodeId = "";
        if (!inputs.TryGetProperty(inputName, out var link) ||
            link.ValueKind != JsonValueKind.Array ||
            link.GetArrayLength() == 0)
        {
            return false;
        }

        linkedNodeId = link[0].ValueKind switch
        {
            JsonValueKind.String => link[0].GetString() ?? "",
            JsonValueKind.Number => link[0].GetRawText(),
            _ => ""
        };

        return !string.IsNullOrWhiteSpace(linkedNodeId);
    }

    private static bool IsKSamplerNode(JsonElement node)
    {
        if (!node.TryGetProperty("class_type", out var classType) ||
            classType.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = classType.GetString();
        return string.Equals(value, "KSampler", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "KSamplerAdvanced", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkflowSamplerNode(JsonElement node)
    {
        if (!node.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = type.GetString();
        return string.Equals(value, "KSampler", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "KSamplerAdvanced", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NodeLooksNegative(JsonElement node)
    {
        var text = node.GetRawText();
        foreach (var marker in NegativeMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string LongestPromptLike(IEnumerable<string> values)
    {
        var best = "";
        foreach (var value in values)
        {
            KeepBestPromptLike(ref best, value);
        }

        return best;
    }

    private static void KeepBestPromptLike(ref string best, string value)
    {
        var cleaned = CleanCandidate(value);
        if (cleaned.Length > best.Length && IsPromptLike(cleaned))
        {
            best = cleaned;
        }
    }

    private static string CleanCandidate(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static bool IsPromptLike(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3)
        {
            return false;
        }

        if (value.StartsWith('{') || value.StartsWith('['))
        {
            return false;
        }

        return value.Any(char.IsLetter);
    }

    private static string ExtractNegativeFromParameters(string parameters)
    {
        var index = parameters.IndexOf("Negative prompt:", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var negText = parameters.Substring(index + "Negative prompt:".Length);
        
        var lastLineIndex = negText.LastIndexOf('\n');
        if (lastLineIndex >= 0)
        {
            var lastLine = negText.Substring(lastLineIndex + 1);
            if (lastLine.Contains("Steps:", StringComparison.OrdinalIgnoreCase))
            {
                negText = negText.Substring(0, lastLineIndex);
            }
        }
        else
        {
            var stepsIndex = negText.IndexOf("Steps:", StringComparison.OrdinalIgnoreCase);
            if (stepsIndex >= 0)
            {
                negText = negText.Substring(0, stepsIndex);
            }
        }

        return CleanCandidate(negText);
    }

    private static string? FindDrawThingsXmp(Dictionary<string, string> metadata)
    {
        foreach (var value in metadata.Values)
        {
            if (value.Contains("Draw Things", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("<dc:description>", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    private static DrawThingsMetadata? ParseDrawThingsXmp(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var liElements = doc.Descendants().Where(d => d.Name.LocalName == "li").ToList();
            
            var descLi = liElements.FirstOrDefault(d => d.Parent?.Parent?.Name.LocalName == "description");
            if (descLi == null)
            {
                descLi = liElements.FirstOrDefault();
            }

            if (descLi == null || string.IsNullOrWhiteSpace(descLi.Value))
            {
                return null;
            }

            var text = descLi.Value.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            int settingsIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains("Steps:", StringComparison.OrdinalIgnoreCase) &&
                    lines[i].Contains("Seed:", StringComparison.OrdinalIgnoreCase))
                {
                    settingsIndex = i;
                    break;
                }
            }

            if (settingsIndex < 0)
            {
                return new DrawThingsMetadata
                {
                    Prompt = text
                };
            }

            string negativePrompt = "";
            var promptLines = new List<string>();

            for (int i = 0; i < settingsIndex; i++)
            {
                var line = lines[i];
                if (line.StartsWith('-'))
                {
                    var negPart = line.Substring(1).Trim();
                    if (string.IsNullOrEmpty(negativePrompt))
                    {
                        negativePrompt = negPart;
                    }
                    else
                    {
                        negativePrompt += "\n" + negPart;
                    }
                }
                else
                {
                    promptLines.Add(line);
                }
            }

            return new DrawThingsMetadata
            {
                Prompt = string.Join("\n", promptLines),
                NegativePrompt = negativePrompt,
                SettingsLine = lines[settingsIndex]
            };
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to parse Draw Things XMP metadata: {ex.Message}");
            return null;
        }
    }

    private sealed class DrawThingsMetadata
    {
        public string Prompt { get; set; } = "";
        public string NegativePrompt { get; set; } = "";
        public string SettingsLine { get; set; } = "";
    }

    private sealed class ComfyPromptMetadata
    {
        public string Prompt { get; set; } = "";
        public string NegativePrompt { get; set; } = "";
        public GenerationSettings? GenerationSettings { get; set; }
    }

    private sealed class WorkflowPromptMetadata
    {
        public string Prompt { get; set; } = "";
        public string NegativePrompt { get; set; } = "";
        public GenerationSettings? GenerationSettings { get; set; }
    }
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
