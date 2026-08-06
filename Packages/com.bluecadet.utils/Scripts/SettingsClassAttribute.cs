using System;

namespace Bluecadet.Utils
{
	/// <summary>
	/// Marks a settings POCO (e.g. a subclass consumed via <see cref="AppEnvironment.SettingsFile{T}"/>)
	/// with the settings base name it corresponds to, so Bluecadet editor tooling can discover the type
	/// and render a typed editor for a given settings base name.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, Inherited = false)]
	public sealed class SettingsClassAttribute : Attribute
	{
		/// <summary>Marks the decorated class as the settings type for <paramref name="baseName"/>.</summary>
		public SettingsClassAttribute(string baseName = "settings")
		{
			BaseName = baseName;
		}

		/// <summary>The settings base name (e.g. <c>"settings"</c>) this class corresponds to.</summary>
		public string BaseName { get; }
	}
}
