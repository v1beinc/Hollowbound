using System;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation
{
    /// <summary>
    /// 2D camera for world rendering with viewport isolation.
    /// Pure math - no MonoGame graphics dependencies in core logic.
    /// </summary>
    public sealed class Camera2D
    {
        public const float MinZoom = 0.25f;
        public const float MaxZoom = 4f;
        public const int DefaultTileSize = 8;
        public const int MarginCells = 2;

        private readonly int _mapWidth;
        private readonly int _mapHeight;
        private readonly int _tileSize;
        private Rectangle _worldViewport;

        // Camera state
        private Vector2 _position; // World position of camera center
        private float _zoom;

        public Camera2D(int mapWidth, int mapHeight, int tileSize, Rectangle worldViewport)
        {
            _mapWidth = mapWidth;
            _mapHeight = mapHeight;
            _tileSize = tileSize;
            _worldViewport = worldViewport;

            _position = new Vector2(mapWidth * tileSize * 0.5f, mapHeight * tileSize * 0.5f);
            _zoom = 1f;
        }

        // Properties
        public Vector2 Position
        {
            get => _position;
            set
            {
                _position = value;
                ClampToMap();
            }
        }
        public float Zoom => _zoom;
        public Rectangle WorldViewport => _worldViewport;
        public int TileSize => _tileSize;

        public void SetViewport(Rectangle viewport)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(viewport), "Camera viewport must have a positive size.");

            _worldViewport = viewport;
            ClampToMap();
        }

        // Zoom control
        public void ZoomAt(float factor, Vector2 screenAnchor)
        {
            var worldAnchor = ScreenToWorld(screenAnchor);
            _zoom = MathHelper.Clamp(_zoom * factor, MinZoom, MaxZoom);
            // Keep world anchor point at same screen position
            _position += worldAnchor - ScreenToWorld(screenAnchor);
            ClampToMap();
        }

        public void ZoomAtCell(float factor, Point cellAnchor)
        {
            var worldAnchor = CellToWorld(cellAnchor);
            var screenAnchor = WorldToScreen(worldAnchor);
            ZoomAt(factor, screenAnchor);
        }

        public void SetZoom(float zoom)
        {
            _zoom = MathHelper.Clamp(zoom, MinZoom, MaxZoom);
            ClampToMap();
        }

        // Pan control
        public void Pan(Vector2 delta)
        {
            _position += delta / _zoom;
            ClampToMap();
        }

        public void PanCells(int dx, int dy)
        {
            _position += new Vector2(dx * _tileSize, dy * _tileSize) / _zoom;
            ClampToMap();
        }

        // Coordinate conversions - use viewport-relative coordinates (viewport origin = 0,0)
        public Vector2 WorldToScreen(Vector2 worldPos)
        {
            var viewportCenter = new Vector2(_worldViewport.Width * 0.5f, _worldViewport.Height * 0.5f);
            return viewportCenter + (worldPos - _position) * _zoom;
        }

        public Vector2 ScreenToWorld(Vector2 screenPos)
        {
            // screenPos is relative to viewport origin (0,0 = viewport top-left)
            var viewportCenter = new Vector2(_worldViewport.Width * 0.5f, _worldViewport.Height * 0.5f);
            return _position + (screenPos - viewportCenter) / _zoom;
        }

        public Point WorldToCell(Vector2 worldPos)
        {
            int x = (int)Math.Floor(worldPos.X / _tileSize);
            int y = (int)Math.Floor(worldPos.Y / _tileSize);
            return new Point(
                Math.Clamp(x, 0, _mapWidth - 1),
                Math.Clamp(y, 0, _mapHeight - 1)
            );
        }

        public Point ScreenToCell(Vector2 screenPos)
        {
            return WorldToCell(ScreenToWorld(screenPos));
        }

        // Picking must reject letterbox space instead of snapping to a border cell.
        public bool TryScreenToCell(Vector2 screenPos, out Point cell)
        {
            var world = ScreenToWorld(screenPos);
            cell = default;
            if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) ||
                world.X < 0 || world.Y < 0 || world.X >= _mapWidth * _tileSize || world.Y >= _mapHeight * _tileSize)
                return false;
            cell = new Point((int)(world.X / _tileSize), (int)(world.Y / _tileSize));
            return true;
        }

        public Vector2 CellToWorld(Point cell)
        {
            return new Vector2(
                (cell.X + 0.5f) * _tileSize,
                (cell.Y + 0.5f) * _tileSize
            );
        }

        // Visible bounds
        public Rectangle GetVisibleCellBounds()
        {
            // Use viewport-relative coordinates (0,0 = viewport top-left)
            var topLeft = ScreenToWorld(new Vector2(0, 0));
            var bottomRight = ScreenToWorld(new Vector2(_worldViewport.Width, _worldViewport.Height));

            int minX = Math.Max(0, (int)Math.Floor(topLeft.X / _tileSize) - MarginCells);
            int minY = Math.Max(0, (int)Math.Floor(topLeft.Y / _tileSize) - MarginCells);
            int maxX = Math.Min(_mapWidth - 1, (int)Math.Ceiling(bottomRight.X / _tileSize) + MarginCells);
            int maxY = Math.Min(_mapHeight - 1, (int)Math.Ceiling(bottomRight.Y / _tileSize) + MarginCells);

            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        public bool IsCellVisible(Point cell)
        {
            var bounds = GetVisibleCellBounds();
            return bounds.Contains(cell);
        }

        // Fit world to viewport
        public void FitWorld()
        {
            float worldWidth = _mapWidth * _tileSize;
            float worldHeight = _mapHeight * _tileSize;

            float zoomX = _worldViewport.Width / worldWidth;
            float zoomY = _worldViewport.Height / worldHeight;
            _zoom = MathHelper.Clamp(Math.Min(zoomX, zoomY), MinZoom, MaxZoom);

            _position = new Vector2(worldWidth * 0.5f, worldHeight * 0.5f);
            ClampToMap();
        }

        public void CenterOnCell(Point cell)
        {
            _position = CellToWorld(cell);
            ClampToMap();
        }

        // Clamping
        private void ClampToMap()
        {
            float halfViewportW = _worldViewport.Width * 0.5f / _zoom;
            float halfViewportH = _worldViewport.Height * 0.5f / _zoom;
            float worldWidth = _mapWidth * _tileSize;
            float worldHeight = _mapHeight * _tileSize;

            // When viewport is larger than world in a dimension, center and don't clamp that dimension
            if (halfViewportW * 2 >= worldWidth)
            {
                _position.X = worldWidth * 0.5f;
            }
            else
            {
                _position.X = MathHelper.Clamp(_position.X, halfViewportW, worldWidth - halfViewportW);
            }

            if (halfViewportH * 2 >= worldHeight)
            {
                _position.Y = worldHeight * 0.5f;
            }
            else
            {
                _position.Y = MathHelper.Clamp(_position.Y, halfViewportH, worldHeight - halfViewportH);
            }
        }

        // Matrix for SpriteBatch
        public Matrix GetViewMatrix()
        {
            var viewportCenter = new Vector2(_worldViewport.Width * 0.5f, _worldViewport.Height * 0.5f);
            var viewportOrigin = new Vector2(_worldViewport.Left, _worldViewport.Top);
            return Matrix.CreateTranslation(new Vector3(-_position, 0)) *
                   Matrix.CreateScale(_zoom) *
                   Matrix.CreateTranslation(new Vector3(viewportOrigin + viewportCenter, 0));
        }

        // Scissor rectangle for culling
        public Rectangle GetScissorRectangle()
        {
            return _worldViewport;
        }
    }
}
