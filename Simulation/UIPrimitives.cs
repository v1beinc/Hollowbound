using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Hollowbound.Simulation
{
    /// <summary>
    /// Minimal UI primitives for text layout and panels.
    /// No event system, no full framework - just reusable helpers.
    /// </summary>
    public static class UIPrimitives
    {
        /// <summary>
        /// Draws a panel with background and optional border.
        /// </summary>
        public static void DrawPanel(SpriteBatch spriteBatch, Texture2D pixel, Rectangle bounds, Color bgColor, Color? borderColor = null, int borderThickness = 1)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            spriteBatch.Draw(pixel, bounds, bgColor);

            if (borderColor.HasValue && borderThickness > 0)
            {
                int t = borderThickness;
                // Top
                spriteBatch.Draw(pixel, new Rectangle(bounds.Left, bounds.Top, bounds.Width, t), borderColor.Value);
                // Bottom
                spriteBatch.Draw(pixel, new Rectangle(bounds.Left, bounds.Bottom - t, bounds.Width, t), borderColor.Value);
                // Left
                spriteBatch.Draw(pixel, new Rectangle(bounds.Left, bounds.Top, t, bounds.Height), borderColor.Value);
                // Right
                spriteBatch.Draw(pixel, new Rectangle(bounds.Right - t, bounds.Top, t, bounds.Height), borderColor.Value);
            }
        }

        /// <summary>
        /// Draws a button with idle/hover/active states.
        /// Returns true if clicked this frame.
        /// </summary>
        public static bool DrawButton(
            SpriteBatch spriteBatch,
            Texture2D pixel,
            SpriteFont font,
            Rectangle bounds,
            string text,
            Color idleColor,
            Color hoverColor,
            Color activeColor,
            Color textColor,
            MouseState mouse,
            MouseState prevMouse,
            bool enabled = true)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            bool isHovered = enabled && bounds.Contains(mouse.Position);
            bool isPressed = isHovered && mouse.LeftButton == ButtonState.Pressed;
            bool wasPressed = isHovered && prevMouse.LeftButton == ButtonState.Pressed;
            bool clicked = wasPressed && mouse.LeftButton == ButtonState.Released && enabled;

            Color bgColor = !enabled ? idleColor * 0.5f : (isPressed ? activeColor : (isHovered ? hoverColor : idleColor));

            DrawPanel(spriteBatch, pixel, bounds, bgColor, UITheme.PanelBorder, UITheme.BasePanelBorder);

            // Center text
            var textSize = font.MeasureString(text);
            var textPos = new Vector2(
                bounds.Center.X - textSize.X * 0.5f,
                bounds.Center.Y - textSize.Y * 0.5f
            );
            spriteBatch.DrawString(font, text, textPos, enabled ? textColor : textColor * 0.5f);

            return clicked;
        }

        /// <summary>
        /// Text alignment options.
        /// </summary>
        public enum TextAlign
        {
            Left,
            Center,
            Right
        }

        /// <summary>
        /// Draws text with word wrapping and clipping to a max width.
        /// Returns the actual height used.
        /// </summary>
        public static float DrawTextWrapped(
            SpriteBatch spriteBatch,
            SpriteFont font,
            string text,
            Vector2 position,
            Color color,
            float maxWidth,
            float lineHeight,
            TextAlign align = TextAlign.Left,
            bool ellipsis = true,
            float scale = 1f)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var words = text.Split(' ');
            var lines = new System.Collections.Generic.List<string>();
            var currentLine = "";

            foreach (var word in words)
            {
                var testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                var size = font.MeasureString(testLine) * scale;

                if (size.X <= maxWidth)
                {
                    currentLine = testLine;
                }
                else
                {
                    if (!string.IsNullOrEmpty(currentLine))
                        lines.Add(currentLine);

                    // Word itself is too long - try to fit with ellipsis
                    if (font.MeasureString(word).X * scale > maxWidth)
                    {
                        if (ellipsis)
                        {
                            var truncated = TruncateWithEllipsis(font, word, maxWidth / scale);
                            lines.Add(truncated);
                        }
                        else
                        {
                            lines.Add(word);
                        }
                        currentLine = "";
                    }
                    else
                    {
                        currentLine = word;
                    }
                }
            }

            if (!string.IsNullOrEmpty(currentLine))
                lines.Add(currentLine);

            float y = position.Y;
            foreach (var line in lines)
            {
                var size = font.MeasureString(line) * scale;
                float x = position.X;

                if (align == TextAlign.Center)
                    x += (maxWidth - size.X) * 0.5f;
                else if (align == TextAlign.Right)
                    x += maxWidth - size.X;

                spriteBatch.DrawString(font, line, new Vector2(x, y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                y += lineHeight;
            }

            return y - position.Y;
        }

        /// <summary>
        /// Draws single-line text with optional ellipsis if too wide.
        /// </summary>
        public static void DrawTextClipped(
            SpriteBatch spriteBatch,
            SpriteFont font,
            string text,
            Vector2 position,
            Color color,
            float maxWidth,
            float scale = 1f,
            TextAlign align = TextAlign.Left,
            bool ellipsis = true)
        {
            if (string.IsNullOrEmpty(text)) return;

            var size = font.MeasureString(text) * scale;

            if (size.X <= maxWidth)
            {
                float x = position.X;
                if (align == TextAlign.Center) x += (maxWidth - size.X) * 0.5f;
                else if (align == TextAlign.Right) x += maxWidth - size.X;

                spriteBatch.DrawString(font, text, new Vector2(x, position.Y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                return;
            }

            if (!ellipsis)
            {
                float x = position.X;
                if (align == TextAlign.Center) x += (maxWidth - size.X) * 0.5f;
                else if (align == TextAlign.Right) x += maxWidth - size.X;

                spriteBatch.DrawString(font, text, new Vector2(x, position.Y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                return;
            }

            // Binary search for max chars that fit with "..."
            int low = 0, high = text.Length;
            string best = "";

            while (low <= high)
            {
                int mid = (low + high) / 2;
                var candidate = text.Substring(0, mid) + "...";
                var candidateSize = font.MeasureString(candidate) * scale;

                if (candidateSize.X <= maxWidth)
                {
                    best = candidate;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (string.IsNullOrEmpty(best))
                best = "...";

            float x2 = position.X;
            if (align == TextAlign.Center) x2 += (maxWidth - font.MeasureString(best).X * scale) * 0.5f;
            else if (align == TextAlign.Right) x2 += maxWidth - font.MeasureString(best).X * scale;

            spriteBatch.DrawString(font, best, new Vector2(x2, position.Y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }

        private static string TruncateWithEllipsis(SpriteFont font, string text, float maxWidth)
        {
            if (font.MeasureString("...").X >= maxWidth) return "";

            int low = 0, high = text.Length;
            string best = "";

            while (low <= high)
            {
                int mid = (low + high) / 2;
                var candidate = text.Substring(0, mid) + "...";
                if (font.MeasureString(candidate).X <= maxWidth)
                {
                    best = candidate;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return string.IsNullOrEmpty(best) ? "..." : best;
        }

        /// <summary>
        /// Measures wrapped text height.
        /// </summary>
        public static float MeasureWrappedHeight(
            SpriteFont font,
            string text,
            float maxWidth,
            float lineHeight)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var words = text.Split(' ');
            var lines = new System.Collections.Generic.List<string>();
            var currentLine = "";

            foreach (var word in words)
            {
                var testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                var size = font.MeasureString(testLine);

                if (size.X <= maxWidth)
                {
                    currentLine = testLine;
                }
                else
                {
                    if (!string.IsNullOrEmpty(currentLine))
                        lines.Add(currentLine);

                    if (font.MeasureString(word).X > maxWidth)
                    {
                        // Word too long - it will be truncated, count as one line
                        lines.Add(word);
                        currentLine = "";
                    }
                    else
                    {
                        currentLine = word;
                    }
                }
            }

            if (!string.IsNullOrEmpty(currentLine))
                lines.Add(currentLine);

            return lines.Count * lineHeight;
        }
    }
}
