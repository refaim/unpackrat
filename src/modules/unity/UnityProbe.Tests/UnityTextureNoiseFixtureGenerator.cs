using System;
using System.IO;
using AssetRipper.SourceGenerated.Enums;

namespace Unpackrat.UnityProbe.Tests;

/// <summary>Creates a deterministic streamed Alpha8 texture through the mature serialized writer.</summary>
public static class UnityTextureNoiseFixtureGenerator
{
	public static string Save(string directory, int width = 8192, int height = 8192)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		int pixelCount = checked(width * height);
		Directory.CreateDirectory(directory);
		const string ResourceName = "noise.resS";
		using (FileStream output = File.Create(Path.Combine(directory, ResourceName)))
		{
			byte[] buffer = new byte[Math.Min(pixelCount, 65536)];
			uint state = 0x17459481;
			for (int remaining = pixelCount; remaining > 0;)
			{
				int count = Math.Min(remaining, buffer.Length);
				for (int i = 0; i < count; i++)
				{
					state ^= state << 13;
					state ^= state >> 17;
					state ^= state << 5;
					buffer[i] = unchecked((byte)state);
				}
				output.Write(buffer, 0, count);
				remaining -= count;
			}
		}
		string assetsPath = Path.Combine(directory, "noise.assets");
		File.WriteAllBytes(assetsPath, UnityFixtureBuilder.SerializedTextureMetadata(width, height, [],
			ResourceName, (uint)pixelCount, TextureFormat.Alpha8));
		return assetsPath;
	}
}
