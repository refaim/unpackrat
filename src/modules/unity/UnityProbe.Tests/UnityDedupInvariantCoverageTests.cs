using System;
using System.Linq;
using System.Reflection;
using AssetRipper.Assets.Collections;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.Primitives;
using Xunit;
using MeshTopology = AssetRipper.SourceGenerated.Enums.MeshTopology;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnityDedupInvariantCoverageTests
{
	private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private static Type Nested(string name)
		=> typeof(UnitySource).GetNestedType(name, BindingFlags.NonPublic)!;

	private static object Construct(Type type, params object?[] arguments)
		=> Activator.CreateInstance(type, Instance, null, arguments, null)!;

	private static uint B(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

	private static object Source(bool present = true, float width = 2f, float height = 2f)
		=> Construct(Nested("SpriteSourceFacts"), present, B(0f), B(0f), B(width), B(height), B(0f), B(0f), B(0f), 2u);

	private static object CanvasFacts()
		=> Construct(Nested("SpriteCanvasFacts"), true, B(2f), B(2f));

	private static object SpriteFacts(object source, object canvas, AssetCollection collection)
	{
		Type type = Nested("SpriteFacts");
		object facts = Activator.CreateInstance(type)!;
		type.GetField("Source")!.SetValue(facts, source);
		type.GetField("Canvas")!.SetValue(facts, canvas);
		type.GetField("SourceCollection")!.SetValue(facts, collection);
		type.GetField("SourceWidth")!.SetValue(facts, 2);
		type.GetField("SourceHeight")!.SetValue(facts, 2);
		type.GetField("Resolved")!.SetValue(facts, true);
		return facts;
	}

	private static bool IsFullTexture(object facts, int width, int height)
		=> (bool)Nested("SpriteFacts").GetMethod("IsFullTexture", Instance)!.Invoke(facts, [width, height])!;

	private static byte[] Pixels()
	{
		byte[] data = new byte[16];
		for (int index = 0; index < data.Length; index++) data[index] = (byte)(index + 1);
		return data;
	}

	private static string Write(UnityFixtureBuilder.TightMeshOptions mesh)
	{
		string path = System.IO.Path.Combine(Temp.NewDirectory(), "sprite.assets");
		System.IO.File.WriteAllBytes(path, UnityFixtureBuilder.SerializedSprite("hero", Pixels(), 2, 2,
			0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 64u, tightMesh: mesh));
		return path;
	}

	private static UnityFixtureBuilder.TightMeshOptions Quad(byte format = 0, int duplicateChannels = 0)
		=> new([-0.01f, -0.01f, 0.01f, -0.01f, 0.01f, 0.01f, -0.01f, 0.01f])
		{
			Indices = [0, 1, 2, 0, 2, 3],
			ChannelDimension = 3,
			ChannelFormat = format,
			ChannelOffset = 0,
			ChannelStream = 0,
			DuplicateChannelCount = duplicateChannels,
			IncludeIndexBuffer = true,
			IncludeSubMesh = true,
			FirstByte = 0,
			IndexCount = 6,
			Topology = MeshTopology.Triangles,
		};

	[Fact]
	public void FullTextureRefusesIncompleteFactsAndNonPositiveCallerDimensions()
	{
		AssetCollection collection = AssetCreator.CreateCollection(UnityVersion.Parse("2021.3.0f1"));
		object full = SpriteFacts(Source(), CanvasFacts(), collection);
		Assert.True(IsFullTexture(full, 2, 2));
		Assert.False(IsFullTexture(full, 0, 2));
		Assert.False(IsFullTexture(full, 2, 0));
		Assert.False(IsFullTexture(SpriteFacts(Source(present: false), CanvasFacts(), collection), 2, 2));
	}

	[Fact]
	public void PlainShapeWithoutTemplateHasNoTraversal()
	{
		Type shape = Nested("Shape");
		object value = Construct(shape, (UnityEntryKind?)UnityEntryKind.Sprite, false, null, null);
		Assert.Null(shape.GetProperty("Traversal", Instance)!.GetValue(value));
		Assert.Null(shape.GetProperty("QuadTraversal", Instance)!.GetValue(value));
	}

	[Fact]
	public void IntegerPositionFormatCannotBeProvenAsAFullQuad()
	{
		using UnitySession session = UnitySession.Open(Write(Quad(format: 6)));
		Assert.Equal(2, session.Entries.Count);
		Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
		Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
	}

	[Fact]
	public void QuadProfileBudgetFailureKeepsTheTextureAndSprite()
	{
		using UnitySession session = UnitySession.Open(Write(Quad(duplicateChannels: 200)));
		Assert.Equal(2, session.Entries.Count);
		Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Texture);
		Assert.Single(session.Entries, entry => entry.Kind == UnityEntryKind.Sprite);
	}
}
