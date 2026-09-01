using System;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace Bluecadet.Utils.Tests
{
	[TestFixture]
	public class YamlTokenReaderTests
	{
		[Test]
		public void ToJObject_NestedMappingsAndSequences_MatchJsonShape()
		{
			JObject result = YamlTokenReader.ToJObject(
				"general:\n" +
				"  debugMode: true\n" +
				"  tags:\n" +
				"    - a\n" +
				"    - b\n" +
				"  window:\n" +
				"    scale: 1.5\n");

			Assert.That((bool)result["general"]["debugMode"], Is.True);
			Assert.That(result["general"]["tags"].ToObject<string[]>(), Is.EqualTo(new[] { "a", "b" }));
			Assert.That((double)result["general"]["window"]["scale"], Is.EqualTo(1.5));
		}

		[Test]
		public void ToJObject_PlainScalars_TypedPerCoreSchema()
		{
			JObject result = YamlTokenReader.ToJObject(
				"anInt: 42\n" +
				"aNegative: -7\n" +
				"aFloat: 3.25\n" +
				"anExponent: 1e3\n" +
				"aLeadingDot: .5\n" +
				"aTrue: True\n" +
				"aFalse: FALSE\n" +
				"aNull: ~\n" +
				"aWordNull: null\n" +
				"aString: hello world\n");

			Assert.That(result["anInt"].Type, Is.EqualTo(JTokenType.Integer));
			Assert.That((long)result["anInt"], Is.EqualTo(42L));
			Assert.That((long)result["aNegative"], Is.EqualTo(-7L));
			Assert.That(result["aFloat"].Type, Is.EqualTo(JTokenType.Float));
			Assert.That((double)result["aFloat"], Is.EqualTo(3.25));
			Assert.That((double)result["anExponent"], Is.EqualTo(1000.0));
			Assert.That((double)result["aLeadingDot"], Is.EqualTo(0.5));
			Assert.That((bool)result["aTrue"], Is.True);
			Assert.That((bool)result["aFalse"], Is.False);
			Assert.That(result["aNull"].Type, Is.EqualTo(JTokenType.Null));
			Assert.That(result["aWordNull"].Type, Is.EqualTo(JTokenType.Null));
			Assert.That((string)result["aString"], Is.EqualTo("hello world"));
		}

		[Test]
		public void ToJObject_HexOctalInfinityNan_Recognized()
		{
			JObject result = YamlTokenReader.ToJObject(
				"aHex: 0xFF\n" +
				"anOctal: 0o17\n" +
				"posInf: .inf\n" +
				"negInf: -.inf\n" +
				"notANumber: .nan\n");

			Assert.That((long)result["aHex"], Is.EqualTo(255L));
			Assert.That((long)result["anOctal"], Is.EqualTo(15L));
			Assert.That((double)result["posInf"], Is.EqualTo(double.PositiveInfinity));
			Assert.That((double)result["negInf"], Is.EqualTo(double.NegativeInfinity));
			Assert.That((double)result["notANumber"], Is.NaN);
		}

		[Test]
		public void ToJObject_YesNoOnOff_StayStrings()
		{
			// YAML 1.1 booleans that 1.2 Core deliberately dropped.
			JObject result = YamlTokenReader.ToJObject("a: yes\nb: no\nc: on\nd: off\n");

			Assert.That((string)result["a"], Is.EqualTo("yes"));
			Assert.That((string)result["b"], Is.EqualTo("no"));
			Assert.That((string)result["c"], Is.EqualTo("on"));
			Assert.That((string)result["d"], Is.EqualTo("off"));
		}

		[Test]
		public void ToJObject_QuotedAndBlockScalars_StayStrings()
		{
			JObject result = YamlTokenReader.ToJObject(
				"quotedTrue: \"true\"\n" +
				"quotedInt: '5'\n" +
				"quotedNull: \"null\"\n" +
				"literal: |\n" +
				"  line1\n" +
				"  line2\n" +
				"folded: >\n" +
				"  one\n" +
				"  two\n");

			Assert.That(result["quotedTrue"].Type, Is.EqualTo(JTokenType.String));
			Assert.That((string)result["quotedTrue"], Is.EqualTo("true"));
			Assert.That((string)result["quotedInt"], Is.EqualTo("5"));
			Assert.That((string)result["quotedNull"], Is.EqualTo("null"));
			Assert.That((string)result["literal"], Is.EqualTo("line1\nline2\n"));
			Assert.That((string)result["folded"], Is.EqualTo("one two\n"));
		}

		[Test]
		public void ToJObject_EmptyAndCommentsOnly_ReturnEmptyObject()
		{
			Assert.That(YamlTokenReader.ToJObject(string.Empty).HasValues, Is.False);
			Assert.That(YamlTokenReader.ToJObject("# just a comment\n").HasValues, Is.False);
			Assert.That(YamlTokenReader.ToJObject("---\n").HasValues, Is.False);
		}

		[Test]
		public void ToJObject_AliasResolvesToAnchoredValue()
		{
			JObject result = YamlTokenReader.ToJObject(
				"shared: &anchor\n" +
				"  x: 1\n" +
				"copy: *anchor\n");

			Assert.That((long)result["copy"]["x"], Is.EqualTo(1L));
		}

		[Test]
		public void ToJObject_DuplicateKey_Throws()
		{
			Assert.That(() => YamlTokenReader.ToJObject("a: 1\na: 2\n"), Throws.Exception);
		}

		[Test]
		public void ToJObject_NonMappingRoot_Throws()
		{
			Assert.That(() => YamlTokenReader.ToJObject("- one\n- two\n"), Throws.TypeOf<FormatException>());
			Assert.That(() => YamlTokenReader.ToJObject("just a scalar\n"), Throws.TypeOf<FormatException>());
		}

		[Test]
		public void ToJObject_CyclicAlias_ThrowsInsteadOfRecursing()
		{
			// A node aliased into itself must be caught by the ancestor guard (or rejected by
			// YamlDotNet outright) — never a stack overflow.
			Assert.That(() => YamlTokenReader.ToJObject("a: &x\n  self: *x\n"), Throws.Exception);
		}

		[Test]
		public void ToJObject_NumericEdgeForms()
		{
			JObject result = YamlTokenReader.ToJObject(
				"signed: +42\n" +
				"padded: 007\n" +
				"bareDot: 1.\n" +
				"huge: 99999999999999999999999\n");

			Assert.That((long)result["signed"], Is.EqualTo(42L));
			Assert.That((long)result["padded"], Is.EqualTo(7L));
			Assert.That(result["bareDot"].Type, Is.EqualTo(JTokenType.Float));
			Assert.That((double)result["bareDot"], Is.EqualTo(1.0));

			// Too large for a long: widened to double rather than silently becoming a string.
			Assert.That(result["huge"].Type, Is.EqualTo(JTokenType.Float));
			Assert.That((double)result["huge"], Is.EqualTo(1e23).Within(1e9));
		}

		[Test]
		public void ToJObject_BlockSequenceOfMappings()
		{
			JObject result = YamlTokenReader.ToJObject(
				"items:\n" +
				"  - x: 1\n" +
				"    y: 2\n" +
				"  - x: 3\n");

			Assert.That((long)result["items"][0]["y"], Is.EqualTo(2L));
			Assert.That((long)result["items"][1]["x"], Is.EqualTo(3L));
		}

		[Test]
		public void ToJObject_MergeKey_ComesThroughAsLiteralKey()
		{
			JObject result = YamlTokenReader.ToJObject(
				"defaults: &d\n" +
				"  x: 1\n" +
				"child:\n" +
				"  <<: *d\n" +
				"  y: 2\n");

			// Merge keys are not interpreted: "<<" is an ordinary key holding the aliased object.
			Assert.That(((JObject)result["child"]).ContainsKey("<<"), Is.True);
			Assert.That((long)result["child"]["<<"]["x"], Is.EqualTo(1L));
			Assert.That(((JObject)result["child"]).ContainsKey("x"), Is.False);
		}
	}
}
