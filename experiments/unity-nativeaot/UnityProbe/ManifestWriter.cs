using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Unpackrat.UnityProbe;

/// <summary>
/// Writes the deterministic export manifest with <see cref="Utf8JsonWriter"/>.
/// </summary>
/// <remarks>
/// Deliberately avoids reflection-based <c>JsonSerializer</c>: this assembly is published with
/// NativeAOT plus <c>IlcDisableReflection=true</c>, so no reflection metadata survives trimming
/// and the serializer cannot run. <see cref="Utf8JsonWriter"/> is reflection-free and AOT-safe.
/// </remarks>
internal static class ManifestWriter
{
	public static void Write(string outputDirectory, string version, int textures, int texts, IReadOnlyList<ManifestResource> resources)
	{
		using FileStream stream = File.Create(Path.Combine(outputDirectory, Manifest.FileName));
		using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });

		writer.WriteStartObject();
		writer.WriteString("schema", Manifest.Schema);
		writer.WriteString("version", version);
		writer.WriteNumber("exported", resources.Count);
		writer.WriteNumber("textures", textures);
		writer.WriteNumber("texts", texts);
		writer.WriteStartArray("resources");
		foreach (ManifestResource resource in resources)
		{
			writer.WriteStartObject();
			writer.WriteString("kind", resource.Kind);
			writer.WriteNumber("pathId", resource.PathId);
			writer.WriteString("file", resource.File);
			writer.WriteNumber("bytes", resource.Bytes);
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteEndObject();
	}
}
