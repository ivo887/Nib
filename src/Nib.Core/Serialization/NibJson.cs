using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nib.Core.Diagnostics;
using Nib.Core.Ink;
using Nib.Core.Layout;
using Nib.Core.Model;

namespace Nib.Core.Serialization;

/// <summary>Source-generated JSON (no reflection: Native AOT and trimming safe, fast startup).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = new[] { typeof(PressureCurveJsonConverter) })]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PageLayout))]
[JsonSerializable(typeof(PageDefaults))]
[JsonSerializable(typeof(BackgroundSpec))]
[JsonSerializable(typeof(PerfTargets))]
[JsonSerializable(typeof(PerfReport))]
[JsonSerializable(typeof(PerfMeasurements))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class NibJsonContext : JsonSerializerContext
{
}

public static class NibJson
{
    public static string Serialize(PageLayout v) => JsonSerializer.Serialize(v, NibJsonContext.Default.PageLayout);
    public static PageLayout? DeserializePageLayout(string? s) =>
        string.IsNullOrEmpty(s) ? null : JsonSerializer.Deserialize(s, NibJsonContext.Default.PageLayout);

    public static string? Serialize(PageDefaults? v) => v is null ? null : JsonSerializer.Serialize(v, NibJsonContext.Default.PageDefaults);
    public static PageDefaults? DeserializePageDefaults(string? s) =>
        string.IsNullOrEmpty(s) ? null : JsonSerializer.Deserialize(s, NibJsonContext.Default.PageDefaults);

    public static string Serialize(AppSettings v) =>
        JsonSerializer.Serialize(v, new NibJsonContext(new JsonSerializerOptions(NibJsonContext.Default.Options) { WriteIndented = true }).AppSettings);

    public static AppSettings? DeserializeSettings(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonSerializer.Deserialize(s, NibJsonContext.Default.AppSettings); }
        catch (JsonException) { return null; }
    }
}

/// <summary>{"kind":"Soft","points":[[0,0],[0.25,0.45],...],"inMin":0,"inMax":1}</summary>
public sealed class PressureCurveJsonConverter : JsonConverter<PressureCurve>
{
    public override PressureCurve Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected object for pressure curve.");
        var kind = PressureCurveKind.Linear;
        float inMin = 0, inMax = 1;
        List<(float, float)>? pts = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            string name = reader.GetString()!;
            reader.Read();
            switch (name)
            {
                case "kind":
                    if (!Enum.TryParse(reader.GetString(), out kind)) kind = PressureCurveKind.Linear;
                    break;
                case "inMin": inMin = reader.GetSingle(); break;
                case "inMax": inMax = reader.GetSingle(); break;
                case "points":
                    pts = new List<(float, float)>();
                    if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        reader.Read(); float x = reader.GetSingle();
                        reader.Read(); float y = reader.GetSingle();
                        reader.Read(); // end of [x,y]
                        pts.Add((x, y));
                    }
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        PressureCurve curve = kind == PressureCurveKind.Custom && pts is { Count: >= 2 }
            ? new PressureCurve(PressureCurveKind.Custom, pts)
            : PressureCurve.FromKind(kind);
        return inMin != 0 || inMax != 1 ? curve.WithCalibration(inMin, inMax) : curve;
    }

    public override void Write(Utf8JsonWriter writer, PressureCurve value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString());
        if (value.Kind == PressureCurveKind.Custom)
        {
            writer.WriteStartArray("points");
            foreach (var (x, y) in value.Points)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(x);
                writer.WriteNumberValue(y);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        writer.WriteNumber("inMin", value.InputMin);
        writer.WriteNumber("inMax", value.InputMax);
        writer.WriteEndObject();
    }
}
