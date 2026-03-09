using UnityEngine;

namespace Bluecadet.Utils
{
	/// <summary>
	/// Static singleton registry that can be used by any MonoBehaviour.
	/// Use this when you need singleton behavior but can't inherit from Singleton&lt;T&gt;.
	/// </summary>
	public static class SingletonRegistry<T> where T : MonoBehaviour
	{
		private static volatile T _instance;
		private static readonly object Lock = new object();

		public static T Get(bool createIfNotFound = false)
		{
			if (Singleton.Quitting)
			{
				Debug.LogWarning($"[Singleton<{typeof(T)}>] Instance will not be returned because the application is quitting.");
				return null;
			}

			// Fast path: instance already cached
			if (_instance != null && _instance)
				return _instance;

			// Slow path: need to find or create
			lock (Lock)
			{
				// Double-check after acquiring lock
				if (_instance != null && _instance)
					return _instance;

				var instances = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
				var count = instances.Length;
				if (count > 0)
				{
					if (count == 1)
						return _instance = instances[0];
					Debug.LogWarning($"[Singleton<{typeof(T)}>] There should never be more than one Singleton of type {typeof(T)} in the scene, but {count} were found. The first instance found will be used, and all others will be destroyed.");
					for (var i = 1; i < instances.Length; i++)
						Object.Destroy(instances[i]);
					return _instance = instances[0];
				}

				if (createIfNotFound)
				{
					Debug.Log($"[Singleton<{typeof(T)}>] An instance is needed in the scene and no existing instances were found, so a new instance will be created.");
					return _instance = new GameObject($"(Singleton){typeof(T)}")
											.AddComponent<T>();
				}

				Debug.LogError($"[Singleton<{typeof(T)}>] No existing instances were found.");
				return null;
			}
		}
	}

	/// <summary>
	/// Thread-safe Singleton base class.
	/// Based on https://gamedev.stackexchange.com/a/151547/159028
	/// </summary>
	public abstract class Singleton<T> : Singleton where T : MonoBehaviour
	{
		public static T Get(bool createIfNotFound = false) => SingletonRegistry<T>.Get(createIfNotFound);
	}

	/// <summary>
	/// Non-generic base for all singletons. Tracks application quit state.
	/// Do not extend directly - use Singleton&lt;T&gt; or SingletonRegistry&lt;T&gt;.
	/// </summary>
	public abstract class Singleton : MonoBehaviour
	{
		public static bool Quitting { get; private set; }

		private void OnApplicationQuit()
		{
			Quitting = true;
		}

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		private static void ResetStaticState() {
				Quitting = false;
		}
	}
}
