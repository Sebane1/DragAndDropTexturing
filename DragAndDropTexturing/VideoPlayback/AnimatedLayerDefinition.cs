using System;
using System.Collections.Generic;
using System.Numerics;

namespace DragAndDropTexturing.VideoPlayback
{
    /// <summary>
    /// Defines a single animated layer that cycles through image frames 
    /// composited at a specific UV position onto a character's texture.
    /// </summary>
    [Serializable]
    public class AnimatedLayerDefinition
    {
        /// <summary>Display name for the UI.</summary>
        public string Name { get; set; } = "Animated Layer";

        /// <summary>Path to folder containing sequential frame images (PNG/JPG/BMP).</summary>
        public string FrameFolder { get; set; } = "";

        /// <summary>Target texture category suffix, e.g. "body", "face".</summary>
        public string TargetCategory { get; set; } = "body";

        /// <summary>UV-space position of the animation overlay (0–1 range). Top-left of placement.</summary>
        public Vector2 UVPosition { get; set; } = new Vector2(0.3f, 0.3f);

        /// <summary>UV-space size of the animation overlay (0–1 range).</summary>
        public Vector2 UVSize { get; set; } = new Vector2(0.4f, 0.4f);

        /// <summary>Playback frame rate.</summary>
        public int Fps { get; set; } = 15;

        /// <summary>Whether this layer is currently active.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>Opacity of the animated overlay (0–1).</summary>
        public float Opacity { get; set; } = 1.0f;
    }
}
