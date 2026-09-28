using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Hollowbound.Simulation
{
    /// <summary>
    /// Centralised UI theme constants - colours, spacing, sizing.
    /// Single source of truth for visual language.
    /// </summary>
    public static class UITheme
    {
        // Base sizes (at 100% UI scale)
        public const int BaseFontSize = 14;
        public const int BasePadding = 8;
        public const int BaseLineHeight = 18;
        public const int BasePanelBorder = 1;
        public const int BaseButtonHeight = 24;
        public const int BaseCornerRadius = 0; // Sharp corners for pixel aesthetic

        // Colours
        public static readonly Color PanelBg = new Color(13, 22, 29, 255);
        public static readonly Color PanelBorder = new Color(39, 57, 65, 255);
        public static readonly Color PrimaryText = new Color(218, 218, 203);      // Warm off-white
        public static readonly Color SecondaryText = new Color(152, 162, 171);    // Muted grey-blue
        public static readonly Color AccentText = new Color(228, 220, 163);       // Warm gold
        public static readonly Color SuccessText = new Color(164, 197, 174);      // Mint green
        public static readonly Color WarningText = new Color(220, 180, 80);       // Amber
        public static readonly Color CriticalText = new Color(220, 80, 80);       // Red
        public static readonly Color ButtonIdle = new Color(22, 27, 34, 255);
        public static readonly Color ButtonHover = new Color(38, 46, 56, 255);
        public static readonly Color ButtonActive = new Color(49, 107, 82, 255);
        public static readonly Color ButtonText = new Color(218, 218, 203);

        // Scale factors
        public static readonly float[] ValidScales = { 0.75f, 1.0f, 1.25f, 1.5f };

        public static float ClampScale(float scale)
        {
            float closest = ValidScales[0];
            float minDiff = Math.Abs(scale - ValidScales[0]);
            for (int i = 1; i < ValidScales.Length; i++)
            {
                float diff = Math.Abs(scale - ValidScales[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = ValidScales[i];
                }
            }
            return closest;
        }

        // Scaled helpers
        public static int Scaled(int baseValue, float uiScale) => (int)Math.Round(baseValue * uiScale);
        public static float Scaled(float baseValue, float uiScale) => baseValue * uiScale;
    }
}
