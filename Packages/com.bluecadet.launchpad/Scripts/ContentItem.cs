namespace Bluecadet.Launchpad
{
	/// <summary>
	/// A single mapped content record: a stable CMS id, a hash of its
	/// canonical serialized attributes (for change detection), and the
	/// app-defined data payload produced by an IContentMapper.
	/// </summary>
	public sealed class ContentItem<T>
	{
		public string Id;
		public ulong ContentHash;
		public T Data;
	}
}
