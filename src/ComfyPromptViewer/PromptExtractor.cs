using System;
using System.Collections.Generic;
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

            foreach (var node in nodes.Values)
            {
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
                    ])
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
        var settings = BuildSettingsSummary([
            FormatSetting("Steps", GetFirst(values, "Steps")),
            FormatSetting("CFG", GetFirst(values, "CFG scale", "CFG", "Guidance Scale")),
            FormatSetting("Scheduler", GetFirst(values, "Schedule type", "Scheduler")),
            FormatSetting("Denoise", GetFirst(values, "Denoising strength", "Denoise", "Strength")),
            FormatSetting("Clip skip", GetFirst(values, "Clip skip"))
        ]);

        var loras = new List<string>();
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
        }
        var lora = string.Join(", ", loras);

        return new GenerationSettings
        {
            Model = model,
            Sampler = sampler,
            Seed = seed,
            Settings = settings,
            Lora = lora
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
                foreach (var node in nodes.EnumerateArray())
                {
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
                    NegativePrompt = LongestPromptLike(negativeCandidates)
                };
            }
        }
        catch (JsonException)
        {
            return new WorkflowPromptMetadata();
        }

        return new WorkflowPromptMetadata();
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

        var start = 0;
        while (start < settingsLine.Length)
        {
            var nextComma = settingsLine.IndexOf(',', start);
            if (nextComma < 0)
            {
                nextComma = settingsLine.Length;
            }

            var part = settingsLine.AsSpan(start, nextComma - start).Trim();
            var separator = part.IndexOf(':');
            if (separator > 0 && separator < part.Length - 1)
            {
                var key = part[..separator].Trim();
                var value = part[(separator + 1)..].Trim();
                if (!key.IsEmpty && !value.IsEmpty)
                {
                    values[key.ToString()] = value.ToString();
                }
            }

            start = nextComma + 1;
        }

        return values;
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
    public string Model { get; set; } = "";
    public string Sampler { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Settings { get; set; } = "";
    public string Lora { get; set; } = "";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Model) &&
                           string.IsNullOrWhiteSpace(Sampler) &&
                           string.IsNullOrWhiteSpace(Seed) &&
                           string.IsNullOrWhiteSpace(Settings) &&
                           string.IsNullOrWhiteSpace(Lora);
}
