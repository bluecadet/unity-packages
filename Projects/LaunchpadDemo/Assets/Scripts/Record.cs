using System;

namespace LaunchpadDemo
{
	/// <summary>
	/// Shared base type for every content model this app maps. One
	/// ContentManager&lt;Record&gt; and one mapper carry all of them as a
	/// single mixed, flat list — see "Multiple content models" in the
	/// com.bluecadet.launchpad README.
	/// </summary>
	[Serializable]
	public abstract class Record
	{
	}
}
