using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Bluecadet.Utils.Editor;

namespace Bluecadet.Utils.Tests
{
	/// <summary>
	/// Covers <see cref="SettingsAnalysis.CollectValidationErrors"/>: the walk that finds every
	/// <see cref="ISettingsValidator"/> in a hydrated settings graph, calls it, and rewrites the relative
	/// paths it reports into the dotted paths the editor tints.
	/// </summary>
	[TestFixture]
	public class SettingsAnalysisValidationTests
	{
		private static List<SettingsValidationError> Collect(object root) =>
			SettingsAnalysis.CollectValidationErrors(root);

		private static string[] PathsOf(IEnumerable<SettingsValidationError> errors)
		{
			var paths = new List<string>();
			foreach (SettingsValidationError error in errors)
				paths.Add(error.Path);

			return paths.ToArray();
		}

		private sealed class RootValidator : ISettingsValidator
		{
			public string label = string.Empty;

			public void Validate(SettingsValidationErrors errors)
			{
				errors.Add(nameof(label), "Label is required.");
				errors.Add(string.Empty, "The settings as a whole are wrong.");
			}
		}

		private sealed class LevelOne
		{
			public LevelTwo display = new();
		}

		private sealed class LevelTwo
		{
			public LevelThree window = new();
		}

		private sealed class LevelThree : ISettingsValidator
		{
			public int width;

			public void Validate(SettingsValidationErrors errors) => errors.Add(nameof(width), "Width must be positive.");
		}

		private sealed class ListHost
		{
			public List<Source> sources = new() { new Source(), new Source() };
			public Source[] extras = { new Source() };
		}

		private sealed class Source : ISettingsValidator
		{
			public string url = string.Empty;

			public void Validate(SettingsValidationErrors errors) => errors.Add(nameof(url), "Url is required.");
		}

		private sealed class SelfReferencing : ISettingsValidator
		{
			public SelfReferencing next;
			public string name = string.Empty;

			public void Validate(SettingsValidationErrors errors) => errors.Add(nameof(name), "Name is required.");
		}

		private sealed class MultiHost
		{
			public LevelThree first = new();
			public Source second = new();
			public LevelThree missing;
		}

		private sealed class UnityValueHost : ISettingsValidator
		{
			public Vector3 position = Vector3.one;
			public Color color = Color.white;

			public void Validate(SettingsValidationErrors errors) => errors.Add(nameof(position), "Position must be zero.");
		}

		[Test]
		public void RootValidator_RelativePathsStayAtTheRoot()
		{
			List<SettingsValidationError> errors = Collect(new RootValidator());

			Assert.That(PathsOf(errors), Is.EqualTo(new[] { "label", string.Empty }));
			Assert.That(errors[0].Message, Is.EqualTo("Label is required."));
		}

		[Test]
		public void NestedValidator_PathIsPrefixedWithItsOwnPath()
		{
			List<SettingsValidationError> errors = Collect(new LevelOne());

			Assert.That(PathsOf(errors), Is.EqualTo(new[] { "display.window.width" }));
		}

		[Test]
		public void MultipleNestedValidators_EachErrorGetsItsOwnPath()
		{
			var root = new MultiHost();

			List<SettingsValidationError> errors = Collect(root);

			Assert.That(PathsOf(errors), Is.EquivalentTo(new[] { "first.width", "second.url" }));
		}

		[Test]
		public void ValidatorInsideListElement_ErrorIsKeyedToTheListPath()
		{
			List<SettingsValidationError> errors = Collect(new ListHost());

			// Arrays are single values in this UI, so every element's error lands on the list field itself.
			Assert.That(PathsOf(errors), Is.EquivalentTo(new[] { "sources", "sources", "extras" }));
		}

		[Test]
		public void NullField_IsSkipped()
		{
			Assert.That(Collect(new MultiHost { first = null, second = null }), Is.Empty);
		}

		[Test]
		public void NullRoot_ReportsNothing()
		{
			Assert.That(Collect(null), Is.Empty);
		}

		[Test]
		public void SelfReferencingObject_IsValidatedOnceAndTerminates()
		{
			var root = new SelfReferencing();
			root.next = root;

			Assert.That(PathsOf(Collect(root)), Is.EqualTo(new[] { "name" }));
		}

		[Test]
		public void MutuallyReferencingObjects_Terminate()
		{
			var first = new SelfReferencing();
			var second = new SelfReferencing { next = first };
			first.next = second;

			Assert.That(PathsOf(Collect(first)), Is.EqualTo(new[] { "name", "next.name" }));
		}

		[Test]
		public void UnityValueTypeFields_DoNotRecurseForever()
		{
			// Color.gamma and Color.linear are both Colors (as Vector3.normalized is a Vector3), so a walk
			// that followed a struct's serialized members would branch until it ran out of time.
			Assert.That(PathsOf(Collect(new UnityValueHost())), Is.EqualTo(new[] { "position" }));
		}

		[Test]
		public void MultipleValidators_ErrorsAggregate()
		{
			var root = new ListHost();

			List<SettingsValidationError> errors = Collect(root);

			Assert.That(errors.Count, Is.EqualTo(3));
			foreach (SettingsValidationError error in errors)
				Assert.That(error.Message, Is.EqualTo("Url is required."));
		}

		[Test]
		public void ErrorToString_FormatsPathAndMessage()
		{
			Assert.That(new SettingsValidationError("a.b", "bad").ToString(), Is.EqualTo("a.b: bad"));
			Assert.That(new SettingsValidationError(string.Empty, "bad").ToString(), Is.EqualTo("bad"));
		}
	}
}
