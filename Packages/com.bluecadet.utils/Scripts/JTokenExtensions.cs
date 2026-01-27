using Newtonsoft.Json.Linq;
using System;

namespace Bluecadet.Utils {

	// Custom exception for model build errors
	// We want to catch these and log them as warnings so we can continue loading the rest of the data
	// System exceptions are fatal, and will be allowed to propagate
	public class JsonValidationException : Exception {
		public string PropertyPath { get; }

		public JsonValidationException(string message, JToken token) : base(message) {
			PropertyPath = token.Path;
		}

		public JsonValidationException(string message, JToken token, string propertyName) : base(message) {
			PropertyPath = $"{token.Path}.{propertyName}";
		}

		public JsonValidationException(string message) : base(message) { }

		public override string ToString() {
			if (string.IsNullOrEmpty(PropertyPath)) {
				return base.ToString();
			}

			return $"{base.ToString()} \n(JSON Property Path: {PropertyPath})";
		}
	}

	public static class JTokenExtensions {
		public static void AssertValueNotTrue(this JToken token, string propertyName) {
			var value = token.OptionalValue<bool>(propertyName, false);
			if (value == true) {
				throw new JsonValidationException($"Field '{propertyName}' must be false.", token, propertyName);
			}
		}

		public static T RequiredValue<T>(this JToken token, string propertyName) {
			return GetValue<T>(token, propertyName, true);
		}

		public static T OptionalValue<T>(this JToken token, string propertyName, T defaultValue = default) {
			return GetValue<T>(token, propertyName, false, defaultValue);
		}

		public static T GetValue<T>(JToken token, string propertyName, bool isRequired, T defaultValue = default) {
			if (token.CheckNullOrUndefined(propertyName)) {
				if (isRequired) {
					throw new JsonValidationException($"Required field '{propertyName}' is missing or null.", token, propertyName);
				}
				return defaultValue;
			}

			// Additional handling for string types
			if (typeof(T) == typeof(string)) {
				string value = token[propertyName].Value<string>();
				if (string.IsNullOrWhiteSpace(value)) {
					if (isRequired) {
						throw new JsonValidationException($"Required field '{propertyName}' is empty or whitespace.", token, propertyName);
					}
					return defaultValue;
				}
				return (T)(object)value;
			}

			try {
				return token[propertyName].Value<T>();
			} catch (Exception ex) {
				if (isRequired) {
					throw new JsonValidationException($"Error parsing required field '{propertyName}': {ex.Message}", token, propertyName);
				}
				return defaultValue;
			}
		}


		public static T RequiredNestedValue<T>(this JToken token, params string[] propertyNames) {
			return GetNestedValue<T>(token, true, propertyNames);
		}

		public static T OptionalNestedValue<T>(this JToken token, params string[] propertyNames) {
			return GetNestedValue<T>(token, false, propertyNames, default);
		}

		public static T GetNestedValue<T>(JToken token, bool isRequired, string[] propertyNames, T defaultValue = default) {
			JToken currentToken = token;

			for (int i = 0; i < propertyNames.Length - 1; i++) {
				currentToken = GetValue<JToken>(currentToken, propertyNames[i], isRequired);

				if (currentToken == null && !isRequired) {
					return defaultValue;
				}
			}

			string lastPropertyName = propertyNames[propertyNames.Length - 1];
			return GetValue<T>(currentToken, lastPropertyName, isRequired, defaultValue);
		}

		public static bool CheckNullOrUndefined(this JToken token, string propertyName) {
			return token == null || token[propertyName] == null || token[propertyName].Type == JTokenType.Null || token[propertyName].Type == JTokenType.Undefined;
		}
	}
}