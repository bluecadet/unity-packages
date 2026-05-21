using UnityEngine;

namespace Bluecadet.UIBlur
{

  public class KawaseDualFilter
  {
    public readonly struct Resolution
    {
      public readonly int Width;
      public readonly int Height;

      public float XTexelSize => Width <= 0 ? 0.0f : 1.0f / Width;
      public float YTexelSize => Height <= 0 ? 0.0f : 1.0f / Height;

      public Resolution(float width, float height)
      {
        Width = Mathf.CeilToInt(width);
        Height = Mathf.CeilToInt(height);
      }
    }

    /// Computes blur pass count and sample offset for a given blur scale.
    /// Model: effective_blur ≈ sampleOffset × 2^actualPasses
    /// The minimum pass count N is chosen so that blurScale / 2^N ≤ maxOffset.
    public static void ComputeBlurParams(float blurScale, int maxPasses, float maxOffset,
        out int actualPasses, out float sampleOffset)
    {
      actualPasses = maxPasses;
      for (int n = 1; n <= maxPasses; n++)
      {
        if (blurScale / Mathf.Pow(2f, n) <= maxOffset)
        {
          actualPasses = n;
          break;
        }
      }
      sampleOffset = blurScale / Mathf.Pow(2f, actualPasses);
    }
  }

}