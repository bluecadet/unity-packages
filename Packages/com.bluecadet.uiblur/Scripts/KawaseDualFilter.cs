using System.Collections.Generic;
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

    public static void GetResolutionsForScale(int width, int height, float scale, ICollection<Resolution> passes)
    {
      passes.Clear();
      var currentWidth = (float)width;
      var currentHeight = (float)height;

      while (scale > 1.0f)
      {
        // Halve the texture size at maximum
        var currentScale = Mathf.Min(scale, 2.0f);
        var currentRatio = 1.0f / currentScale;
        currentWidth *= currentRatio;
        currentHeight *= currentRatio;
        passes.Add(new Resolution(currentWidth, currentHeight));
        scale *= currentRatio;
      }
    }
  }

}