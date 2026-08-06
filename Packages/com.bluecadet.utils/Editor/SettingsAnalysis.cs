using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Bluecadet.Utils.Editor
{
	/// <summary>
	/// The pure inspection behind the settings editor: which dotted leaf paths a settings JSON object has,
	/// which of them changed between two snapshots of it, and what the <see cref="ISettingsValidator"/>s in a
	/// hydrated settings instance report. Split out of <see cref="SettingsEditorPane"/> so none of it needs
	/// a GUI to run or to test.
	/// </summary>
	internal static class SettingsAnalysis
	{
		/// <summary>Collects every dotted leaf path in <paramref name="root"/>, per <see cref="SettingsPath.IsLeaf"/>.</summary>
		internal static void FlattenPaths(JObject root, HashSet<string> paths)
		{
			Flatten(root, SettingsPath.Root, paths);
		}

		/// <summary>
		/// Adds the dotted path of every leaf in <paramref name="after"/> whose value differs from the
		/// corresponding leaf in <paramref name="before"/> to <paramref name="changed"/>. A leaf missing from
		/// <paramref name="before"/> counts as changed.
		/// </summary>
		internal static void CollectChangedLeaves(JObject before, JObject after, List<string> changed)
		{
			CollectChangedLeaves(before, after, SettingsPath.Root, changed);
		}

		/// <summary>
		/// Walks <paramref name="root"/>'s object graph over the same member surface Json.NET serializes,
		/// calls <see cref="ISettingsValidator.Validate"/> on every object that implements it (the root
		/// included), and returns what they reported with each relative path prefixed by the dotted path
		/// of the object that reported it. Errors coming out of a list or array element are keyed to the
		/// list's own path: arrays are single values in this UI, so that is the finest path a tint can
		/// land on.
		/// </summary>
		internal static List<SettingsValidationError> CollectValidationErrors(object root)
		{
			var errors = new List<SettingsValidationError>();
			Validate(root, SettingsPath.Root, insideList: false, new HashSet<object>(ReferenceComparer.Instance), errors);
			return errors;
		}

		private static void Flatten(JObject obj, SettingsPath prefix, HashSet<string> paths)
		{
			foreach (JProperty property in obj.Properties())
			{
				SettingsPath path = prefix.Append(property.Name);

				if (SettingsPath.IsLeaf(property.Value))
					paths.Add(path.ToString());
				else
					Flatten((JObject)property.Value, path, paths);
			}
		}

		private static void CollectChangedLeaves(JObject before, JObject after, SettingsPath prefix, List<string> changed)
		{
			foreach (JProperty property in after.Properties())
			{
				SettingsPath path = prefix.Append(property.Name);
				JToken beforeValue = before?[property.Name];

				if (!SettingsPath.IsLeaf(property.Value))
				{
					CollectChangedLeaves(beforeValue as JObject ?? new JObject(), (JObject)property.Value, path, changed);
					continue;
				}

				if (!JToken.DeepEquals(beforeValue, property.Value))
					changed.Add(path.ToString());
			}
		}

		/// <summary>
		/// Validates <paramref name="value"/> and everything below it. <paramref name="insideList"/> freezes
		/// the reported path at the enclosing list's path, and the visited set both keeps a reference cycle
		/// (a settings object pointing back at an ancestor) from recursing forever and keeps a shared object
		/// from being validated twice.
		/// </summary>
		private static void Validate(
			object value,
			SettingsPath path,
			bool insideList,
			HashSet<object> visited,
			List<SettingsValidationError> errors)
		{
			if (value == null || value is string)
				return;

			Type type = value.GetType();

			if (!type.IsValueType && !visited.Add(value))
				return;

			if (value is ISettingsValidator validator)
				Collect(validator, path, keepRelativePaths: !insideList, errors);

			// A struct is boxed anew on every read, so the visited set cannot recognize it, and Unity's math
			// types expose computed properties that hand back more of themselves (Color.gamma, Color.linear,
			// Vector3.normalized, ...) — walking into them never ends. Known limitation: a validator nested
			// inside a struct is not reached, though a struct that is itself a validator still runs.
			if (type.IsValueType)
				return;

			JsonContract contract = SettingsJson.Serializer.ContractResolver.ResolveContract(type);

			if (contract is JsonDictionaryContract && value is IDictionary dictionary)
			{
				foreach (object element in dictionary.Values)
					Validate(element, path, insideList: true, visited, errors);

				return;
			}

			if (contract is JsonArrayContract && value is IEnumerable enumerable)
			{
				foreach (object element in enumerable)
					Validate(element, path, insideList: true, visited, errors);

				return;
			}

			if (!(contract is JsonObjectContract objectContract))
				return;

			foreach (JsonProperty property in objectContract.Properties)
			{
				if (property.Ignored || !property.Readable)
					continue;

				object child;
				try
				{
					child = property.ValueProvider.GetValue(value);
				}
				catch
				{
					// A property that throws on read has no value to validate, and it is not serialized either.
					continue;
				}

				Validate(child, insideList ? path : path.Append(property.PropertyName), insideList, visited, errors);
			}
		}

		/// <summary>
		/// Runs one validator and appends its errors with <paramref name="path"/> prefixed onto each relative
		/// path. Inside a list <paramref name="keepRelativePaths"/> is false and every error is keyed to
		/// <paramref name="path"/> itself: the list is a single value in this UI, so an element's own field
		/// names are not paths the editor could tint.
		/// </summary>
		private static void Collect(ISettingsValidator validator, SettingsPath path, bool keepRelativePaths, List<SettingsValidationError> errors)
		{
			var reported = new SettingsValidationErrors();

			try
			{
				validator.Validate(reported);
			}
			catch (Exception ex)
			{
				errors.Add(new SettingsValidationError(path.ToString(), $"{validator.GetType().Name}.Validate threw: {ex.Message}"));
				return;
			}

			foreach (SettingsValidationError error in reported.Errors)
			{
				string errorPath = keepRelativePaths ? path.Append(error.Path).ToString() : path.ToString();
				errors.Add(new SettingsValidationError(errorPath, error.Message));
			}
		}

		/// <summary>Identity comparer for the validation walk's visited set, so two equal-but-distinct settings objects are both visited.</summary>
		private sealed class ReferenceComparer : IEqualityComparer<object>
		{
			internal static readonly ReferenceComparer Instance = new ReferenceComparer();

			bool IEqualityComparer<object>.Equals(object left, object right) => ReferenceEquals(left, right);

			int IEqualityComparer<object>.GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
		}
	}
}
