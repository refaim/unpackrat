using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Assets.IO;
using AssetRipper.Assets.Metadata;
using AssetRipper.IO.Files.SerializedFiles;
using AssetRipper.IO.Files.SerializedFiles.Parser;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Extensions;
using Xunit;

namespace Unpackrat.UnityProbe.Tests;

public sealed class UnitySourceFinalContractTests
{
	private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

	private static Type Nested(string name)
		=> typeof(UnitySource).GetNestedType(name, BindingFlags.NonPublic)!;

	private static object Construct(Type type, params object?[] arguments)
		=> Activator.CreateInstance(type, Instance, null, arguments, null)!;

	private static uint B(float value) => unchecked((uint)BitConverter.SingleToInt32Bits(value));

	private static object Source(
		bool present = true,
		float width = 2f,
		float height = 2f,
		float offsetX = 0f,
		float offsetY = 0f)
		=> Construct(Nested("SpriteSourceFacts"), present, B(0f), B(0f), B(width), B(height),
			B(offsetX), B(offsetY), B(0f), 2u);

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

	private static bool Comparable(object facts)
		=> (bool)facts.GetType().GetProperty("Comparable", Instance)!.GetValue(facts)!;

	private static bool IsFullTexture(object facts, int width, int height)
		=> (bool)Nested("SpriteFacts").GetMethod("IsFullTexture", Instance)!
			.Invoke(facts, new object?[] { width, height })!;

	private static byte[] Pixels()
	{
		byte[] data = new byte[16];
		for (int index = 0; index < data.Length; index++) data[index] = (byte)(index + 1);
		return data;
	}

	private static string WriteRectangleSprite()
	{
		string path = Path.Combine(Temp.NewDirectory(), "sprite.assets");
		File.WriteAllBytes(path, UnityFixtureBuilder.SerializedSprite("hero", Pixels(), 2, 2,
			0, 0, 2, 2, 0, 0, 2, 2, 0, 0, settingsRaw: 2u));
		return path;
	}

	private static object SingleCompactCollection(object graph)
	{
		object factory = graph.GetType().GetField("factory", Instance)!.GetValue(graph)!;
		IDictionary collections = (IDictionary)factory.GetType()
			.GetField("compactCollections", Instance)!.GetValue(factory)!;
		foreach (DictionaryEntry entry in collections) return entry.Value!;
		throw new InvalidOperationException("The fixture must create one compact collection.");
	}

	[Fact]
	public void FullTextureRejectsNonZeroTrimOffsetsThatRoundToTheCanvasOrigin()
	{
		AssetCollection collection = AssetCreator.CreateCollection(UnityVersion.Parse("2021.3.0f1"));
		object canvas = CanvasFacts();
		object zero = SpriteFacts(Source(), canvas, collection);
		Assert.True(Comparable(zero));
		Assert.True(IsFullTexture(zero, 2, 2));

		object xPositive = SpriteFacts(Source(offsetX: float.Epsilon), canvas, collection);
		object xNegative = SpriteFacts(Source(offsetX: -float.Epsilon), canvas, collection);
		object x = Comparable(xPositive) ? xPositive : xNegative;
		Assert.True(Comparable(x), "A near-zero horizontal trim offset must remain exportable.");
		Assert.False(IsFullTexture(x, 2, 2));

		object yPositive = SpriteFacts(Source(offsetY: float.Epsilon), canvas, collection);
		object yNegative = SpriteFacts(Source(offsetY: -float.Epsilon), canvas, collection);
		object y = Comparable(yPositive) ? yPositive : yNegative;
		Assert.True(Comparable(y), "A near-zero vertical trim offset must remain exportable.");
		Assert.False(IsFullTexture(y, 2, 2));
	}

	[Fact]
	public void QuadProofFailureBeforeScratchAllocationDoesNotThrow()
	{
		string selected = WriteRectangleSprite();
		var graph = UnitySource.OpenGraph(selected, readFile: null, fullReader: new NullReader(), compact: true);
		using UnitySession session = UnitySession.FromGraph(graph);
		Assert.NotEmpty(session.Entries);

		object collection = SingleCompactCollection(graph);
		Array rows = (Array)collection.GetType().GetField("rows", Instance)!.GetValue(collection)!;
		IList prototypes = (IList)collection.GetType().GetField("prototypes", Instance)!.GetValue(collection)!;
		int rowIndex = -1;
		object row = null!;
		object shape = null!;
		for (int index = 0; index < rows.Length; index++)
		{
			object candidate = rows.GetValue(index)!;
			int prototypeIndex = (int)candidate.GetType().GetField("Prototype", Instance)!.GetValue(candidate)!;
			object prototype = prototypes[prototypeIndex]!;
			if ((int)prototype.GetType().GetProperty("ClassID", Instance)!.GetValue(prototype)! != 213) continue;
			rowIndex = index;
			row = candidate;
			shape = prototype.GetType().GetProperty("Shape", Instance)!.GetValue(prototype)!;
			break;
		}
		Assert.True(rowIndex >= 0, "The fixture must contain a Sprite payload row.");
		Assert.Null(shape.GetType().GetField("quadTraversal", Instance)!.GetValue(shape));

		row.GetType().GetField("Offset", Instance)!.SetValue(row, 1_000_000L);
		rows.SetValue(row, rowIndex);

		MethodInfo prove = collection.GetType().GetMethod("TryProveQuad", Instance)!;
		Assert.False((bool)prove.Invoke(collection, new object?[] { rowIndex, 2, 2 })!);

		object state = shape.GetType().GetField("quadTraversal", Instance)!.GetValue(shape)!;
		Assert.NotNull(state);
		Assert.Null(state.GetType().GetProperty("Scratch", Instance)!.GetValue(state));
	}

	private sealed class NullReader : AssetFactoryBase
	{
		public override IUnityObjectBase? ReadAsset(AssetInfo info, ReadOnlyArraySegment<byte> data,
			SerializedType? type, IReadOnlyList<SerializedTypeReference> references) => null;
	}
}
