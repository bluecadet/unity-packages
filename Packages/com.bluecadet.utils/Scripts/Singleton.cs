using UnityEngine;

namespace Bluecadet.Utils
{

	// thread safe Singleton ABC. 
	// based on https://gamedev.stackexchange.com/a/151547/159028
	public abstract class Singleton<T> : Singleton where T : MonoBehaviour
	{
		private static T _instance;

		private static readonly object Lock = new object();

		public static T Get(bool createIfNotFound = false)
		{
			if (Quitting)
			{
				Debug.LogWarning($"[{nameof(Singleton)}<{typeof(T)}>] Instance will not be returned because the application is quitting.");
				return null;
			}
			lock (Lock)
			{
				if (_instance != null)
					return _instance;
				var instances = FindObjectsByType<T>(FindObjectsSortMode.None);
				var count = instances.Length;
				if (count > 0)
				{
					if (count == 1)
						return _instance = instances[0];
					Debug.LogWarning($"[{nameof(Singleton)}<{typeof(T)}>] There should never be more than one {nameof(Singleton)} of type {typeof(T)} in the scene, but {count} were found. The first instance found will be used, and all others will be destroyed.");
					for (var i = 1; i < instances.Length; i++)
						Destroy(instances[i]);
					return _instance = instances[0];
				}

				if (createIfNotFound)
				{
					Debug.Log($"[{nameof(Singleton)}<{typeof(T)}>] An instance is needed in the scene and no existing instances were found, so a new instance will be created.");
					return _instance = new GameObject($"({nameof(Singleton)}){typeof(T)}")
											.AddComponent<T>();
				}

				Debug.LogError($"[{nameof(Singleton)}<{typeof(T)}>] No existing instances were found.");
				return null;
			}
		}
	}

	// Do not extend Singleton directly - use the Singleton<> Generic
	public abstract class Singleton : MonoBehaviour
	{
		public static bool Quitting { get; private set; }

		private void OnApplicationQuit()
		{
			Quitting = true;
		}
	}
}
