using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Bluecadet.Utils.Tests
{
	[TestFixture]
	public class SettingsJsonTests
	{
		private sealed class VectorSettings
		{
			public Vector3 position = new Vector3(1f, 2f, 3f);
		}

		[Test]
		public void Serializer_UnityVectorField_SerializesWithoutSelfReferencingLoop()
		{
			// Vector3.normalized returns another Vector3, which the default serializer reports as a
			// self-referencing loop.
			JObject json = JObject.FromObject(new VectorSettings(), SettingsJson.Serializer);

			Assert.That((float)json["position"]["x"], Is.EqualTo(1f));
		}

		[Test]
		public void Serializer_UnityVectorField_RoundTrips()
		{
			JObject json = JObject.FromObject(new VectorSettings(), SettingsJson.Serializer);

			var restored = json.ToObject<VectorSettings>(SettingsJson.Serializer);

			Assert.That(restored.position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
		}
	}
}
