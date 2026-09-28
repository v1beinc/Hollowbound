using System;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation
{
    /// <summary>
    /// Technical layout contract - computes UI rectangles from window size and UI scale.
    /// No rendering logic, just geometry.
    /// </summary>
    public sealed class GameLayout
    {
        // Base sizes at 100% scale
        public const int BaseTopBarHeight = 76;
        public const int BaseLeftPanelWidth = 228;
        public const int BaseRightPanelWidth = 310;
        public const int BaseBottomPanelHeight = 118;

        // Minimum physical (screen-space) widths/heights regardless of UI scale
        public const int MinLeftPanelWidth = 160;
        public const int MinRightPanelWidth = 240;
        public const int MinTopBarHeight = 36;
        public const int MinBottomPanelHeight = 100;
        public const int MinWorldViewportWidth = 160;
        public const int MinWorldViewportHeight = 120;
        public const int MinWindowWidth = 640;
        public const int MinWindowHeight = 360;

        private readonly Rectangle _windowBounds;
        private readonly float _uiScale;

        public GameLayout(Rectangle windowBounds, float uiScale = 1.0f)
        {
            _windowBounds = windowBounds;
            _uiScale = uiScale;
        }

        public Rectangle WindowBounds => _windowBounds;
        public float UIScale => _uiScale;

        private int Scaled(int baseValue) => (int)Math.Round(baseValue * _uiScale);

        public int TopBarHeight => Math.Max(MinTopBarHeight, Scaled(BaseTopBarHeight));
        public int LeftPanelWidth => Math.Max(MinLeftPanelWidth, Scaled(BaseLeftPanelWidth));
        public int RightPanelWidth => Math.Max(MinRightPanelWidth, Scaled(BaseRightPanelWidth));
        public int BottomPanelHeight => Math.Max(MinBottomPanelHeight, Scaled(BaseBottomPanelHeight));

        private int EffectiveTopBarHeight => Math.Min(TopBarHeight, Math.Max(0, Math.Min(_windowBounds.Height - 1, _windowBounds.Height / 6)));
        private int EffectiveBottomPanelHeight => Math.Min(BottomPanelHeight, Math.Max(0, Math.Min(_windowBounds.Height - EffectiveTopBarHeight - 1, _windowBounds.Height / 5)));
        private float HorizontalPanelCompression
        {
            get
            {
                var desired = LeftPanelWidth + RightPanelWidth;
                var available = Math.Max(0, _windowBounds.Width - MinWorldViewportWidth);
                return desired <= 0 || desired <= available ? 1f : (float)available / desired;
            }
        }

        private int EffectiveLeftPanelWidth => (int)Math.Floor(LeftPanelWidth * HorizontalPanelCompression);
        private int EffectiveRightPanelWidth => Math.Min(
            (int)Math.Floor(RightPanelWidth * HorizontalPanelCompression),
            Math.Max(0, _windowBounds.Width - EffectiveLeftPanelWidth - 1));

        public Rectangle TopBar => new Rectangle(0, 0, _windowBounds.Width, EffectiveTopBarHeight);

        public Rectangle RightPanel =>
            new Rectangle(
                _windowBounds.Width - EffectiveRightPanelWidth,
                EffectiveTopBarHeight,
                EffectiveRightPanelWidth,
                _windowBounds.Height - EffectiveTopBarHeight - EffectiveBottomPanelHeight
            );

        public Rectangle LeftPanel =>
            new Rectangle(
                0,
                EffectiveTopBarHeight,
                EffectiveLeftPanelWidth,
                _windowBounds.Height - EffectiveTopBarHeight - EffectiveBottomPanelHeight
            );

        public Rectangle BottomPanel =>
            new Rectangle(0, _windowBounds.Height - EffectiveBottomPanelHeight, _windowBounds.Width, EffectiveBottomPanelHeight);

        public Rectangle WorldViewport =>
            new Rectangle(
                EffectiveLeftPanelWidth,
                EffectiveTopBarHeight,
                Math.Max(1, _windowBounds.Width - EffectiveLeftPanelWidth - EffectiveRightPanelWidth),
                Math.Max(1, _windowBounds.Height - EffectiveTopBarHeight - EffectiveBottomPanelHeight)
            );

        public bool IsPointInWorldViewport(Point point) => WorldViewport.Contains(point);

        public bool IsPointInWorldViewport(Vector2 point) => WorldViewport.Contains((int)point.X, (int)point.Y);

        public GameLayout WithWindowBounds(Rectangle newBounds) => new GameLayout(newBounds, _uiScale);

        public GameLayout WithUIScale(float newScale) => new GameLayout(_windowBounds, newScale);
    }
}
