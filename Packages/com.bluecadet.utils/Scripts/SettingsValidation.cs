using System.Collections.Generic;

namespace Bluecadet.Utils
{
	/// <summary>
	/// Implemented by a settings class (or by any object nested inside one) to report values that are
	/// missing or out of range. Bluecadet editor tooling walks the hydrated settings graph, calls
	/// <see cref="Validate"/> on every object that implements this interface, and highlights the
	/// offending fields. Validation is editor-only: <see cref="SettingsFile{T}"/> never calls it while
	/// loading, so a build always gets whatever the files say.
	/// </summary>
	public interface ISettingsValidator
	{
		/// <summary>
		/// Reports every problem with this object's values into <paramref name="errors"/>, using paths
		/// relative to this object (the caller prefixes them with the object's own path).
		/// </summary>
		void Validate(SettingsValidationErrors errors);
	}

	/// <summary>One problem reported by an <see cref="ISettingsValidator"/>.</summary>
	public readonly struct SettingsValidationError
	{
		/// <summary>Creates an error at <paramref name="path"/>; an empty path means the object itself.</summary>
		public SettingsValidationError(string path, string message)
		{
			Path = path ?? string.Empty;
			Message = message ?? string.Empty;
		}

		/// <summary>The dotted settings path the error belongs to (e.g. <c>"general.controllerUrl"</c>).</summary>
		public string Path { get; }

		/// <summary>Human-readable description of what is wrong with the value at <see cref="Path"/>.</summary>
		public string Message { get; }

		/// <summary>Formats the error as <c>"path: message"</c>, or just the message when the path is empty.</summary>
		public override string ToString() => string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
	}

	/// <summary>
	/// The collector an <see cref="ISettingsValidator"/> reports into. Paths are relative to the
	/// validating object, so a nested object can validate its own fields without knowing where it
	/// sits in the settings graph.
	/// </summary>
	public sealed class SettingsValidationErrors
	{
		private readonly List<SettingsValidationError> _errors = new();

		/// <summary>Every error reported so far, in the order they were added.</summary>
		public IReadOnlyList<SettingsValidationError> Errors => _errors;

		/// <summary>True when at least one error has been reported.</summary>
		public bool HasErrors => _errors.Count > 0;

		/// <summary>
		/// Reports <paramref name="message"/> against <paramref name="relativePath"/>, a dotted path
		/// relative to the validating object (e.g. <c>"controllerUrl"</c>, or <c>""</c> for the object
		/// as a whole).
		/// </summary>
		public void Add(string relativePath, string message) =>
			_errors.Add(new SettingsValidationError(relativePath, message));
	}
}
