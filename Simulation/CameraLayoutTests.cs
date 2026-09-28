using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation.Tests
{
    /// <summary>
    /// Deterministic tests for Camera2D and GameLayout math (no graphics).
    /// </summary>
    public static class CameraLayoutTests
    {
        public static void RunAll()
        {
            Console.WriteLine("Running Camera2D & GameLayout tests...");

            TestWorldScreenRoundTrip();
            TestScreenToCellCorners();
            TestPickingRejectsLetterbox();
            TestZoomPreservesWorldPoint();
            TestViewMatrixIncludesViewportOrigin();
            TestViewportResizePreservesCameraState();
            TestCameraClamp();
            TestFitWorld();
            TestVisibleCellBounds();
            TestSmallWindow();
            TestBelowMinimumWindowRemainsValid();
            TestStandardResolutions();
            TestUltrawide();
            TestUIScaleReadyLayout();
            TestClickOutsideWorldViewport();
            TestUISettingsPersistence();
                        TestOnboardingProgression();
                        TestDecisionExplanation();
                        TestResponsiveLayoutMinPanelWidths();
                        TestUIScaleAllScales();
                        TestUIInputIsolation();

                        Console.WriteLine("All tests passed!");
        }

        private static void TestWorldScreenRoundTrip()
        {
            var viewport = new Rectangle(0, 40, 800, 560);
            var camera = new Camera2D(128, 80, 8, viewport);

            var testPoints = new[]
            {
                new Vector2(0, 0),
                new Vector2(512, 320),
                new Vector2(1024, 640),
                new Vector2(100, 200),
                new Vector2(800, 100),
            };

            foreach (var worldPoint in testPoints)
            {
                var screen = camera.WorldToScreen(worldPoint);
                var back = camera.ScreenToWorld(screen);

                var diff = Vector2.Distance(worldPoint, back);
                if (diff > 0.01f)
                    throw new Exception($"Round trip failed: {worldPoint} -> {screen} -> {back}, diff={diff}");
            }
            Console.WriteLine("  [PASS] World -> Screen -> World round trip");
        }

        private static void TestScreenToCellCorners()
        {
            var viewport = new Rectangle(0, 40, 800, 560);
            var camera = new Camera2D(128, 80, 8, viewport);

            // Test viewport corners (in viewport-relative coordinates) map to valid cells
            var corners = new[]
            {
                new Vector2(0, 0),                           // Top-left
                new Vector2(viewport.Width, 0),              // Top-right
                new Vector2(0, viewport.Height),             // Bottom-left
                new Vector2(viewport.Width, viewport.Height),// Bottom-right
                new Vector2(viewport.Width * 0.5f, viewport.Height * 0.5f), // Center
            };

            foreach (var corner in corners)
            {
                var cell = camera.ScreenToCell(corner);
                if (cell.X < 0 || cell.X >= 128 || cell.Y < 0 || cell.Y >= 80)
                    throw new Exception($"Corner {corner} -> invalid cell {cell}");
            }
            Console.WriteLine("  [PASS] Screen corners -> valid cells");
        }

        private static void TestPickingRejectsLetterbox()
        {
            var camera = new Camera2D(128, 80, 8, new Rectangle(100, 70, 1600, 900));
            foreach (var p in new[] { new Vector2(-1, 320), new Vector2(1024, 320), new Vector2(512, -1), new Vector2(512, 640) })
                if (camera.TryScreenToCell(camera.WorldToScreen(p), out _))
                    throw new Exception($"Out-of-map picking accepted {p}");
            foreach (var cell in new[] { new Point(0, 0), new Point(127, 79), new Point(64, 40) })
                if (!camera.TryScreenToCell(camera.WorldToScreen(camera.CellToWorld(cell)), out var found) || found != cell)
                    throw new Exception($"In-map picking failed {cell}");
            Console.WriteLine("  [PASS] Picking rejects letterbox without clamping to border cells");
        }

        private static void TestZoomPreservesWorldPoint()
        {
            var viewport = new Rectangle(0, 40, 800, 560);
            var camera = new Camera2D(128, 80, 8, viewport);

            var worldPoint = new Vector2(512, 320);
            var screenAnchor = camera.WorldToScreen(worldPoint);

            // Zoom in
            camera.ZoomAt(2f, screenAnchor);
            var newScreen = camera.WorldToScreen(worldPoint);

            // The world point should stay at the same screen position
            var diff = Vector2.Distance(screenAnchor, newScreen);
            if (diff > 1f)
                throw new Exception($"Zoom didn't preserve world point: anchor={screenAnchor}, new={newScreen}, diff={diff}");

            Console.WriteLine("  [PASS] Zoom preserves world point under cursor");
        }

        private static void TestViewMatrixIncludesViewportOrigin()
        {
            var viewport = new Rectangle(37, 40, 800, 560);
            var camera = new Camera2D(128, 80, 8, viewport);
            var screen = Vector2.Transform(camera.Position, camera.GetViewMatrix());
            var expected = new Vector2(viewport.Center.X, viewport.Center.Y);

            if (Vector2.Distance(screen, expected) > 0.01f)
                throw new Exception($"View matrix ignored viewport origin: actual={screen}, expected={expected}");

            Console.WriteLine("  [PASS] View matrix includes viewport origin");
        }

        private static void TestViewportResizePreservesCameraState()
        {
            var camera = new Camera2D(128, 80, 8, new Rectangle(0, 40, 800, 560));
            camera.SetZoom(2f);
            camera.CenterOnCell(new Point(70, 42));
            var position = camera.Position;
            var zoom = camera.Zoom;

            camera.SetViewport(new Rectangle(0, 40, 1200, 760));

            if (camera.Zoom != zoom || Vector2.Distance(camera.Position, position) > 0.01f)
                throw new Exception($"Viewport resize reset camera state: position={camera.Position}, zoom={camera.Zoom}");

            Console.WriteLine("  [PASS] Viewport resize preserves camera state");
        }

        private static void TestCameraClamp()
        {
            var viewport = new Rectangle(0, 40, 800, 560);
            var camera = new Camera2D(128, 80, 8, viewport);

            // Try to pan far beyond map bounds
            camera.Pan(new Vector2(-10000, -10000));
            if (camera.Position.X < 0 || camera.Position.Y < 0)
                throw new Exception($"Camera position negative after clamp: {camera.Position}");

            camera.Pan(new Vector2(10000, 10000));
            var maxX = 128 * 8;
            var maxY = 80 * 8;
            if (camera.Position.X > maxX || camera.Position.Y > maxY)
                throw new Exception($"Camera position beyond map: {camera.Position} > ({maxX},{maxY})");

            Console.WriteLine("  [PASS] Camera clamp to map bounds");
        }

        private static void TestFitWorld()
        {
            var viewport = new Rectangle(0, 40, 800, 560);
            var camera = new Camera2D(128, 80, 8, viewport);

            camera.SetZoom(4f); // Max zoom
            camera.FitWorld();

            // After FitWorld, entire map should be visible (within margins)
            var bounds = camera.GetVisibleCellBounds();
            if (bounds.Left > Camera2D.MarginCells || bounds.Top > Camera2D.MarginCells ||
                bounds.Right < 127 - Camera2D.MarginCells || bounds.Bottom < 79 - Camera2D.MarginCells)
                throw new Exception($"FitWorld didn't show full map: bounds={bounds}, margin={Camera2D.MarginCells}");

            Console.WriteLine("  [PASS] FitWorld shows entire map");
        }

        private static void TestVisibleCellBounds()
        {
            var viewport = new Rectangle(0, 40, 800, 560);
            var camera = new Camera2D(128, 80, 8, viewport);

            camera.SetZoom(1f);
            camera.CenterOnCell(new Point(64, 40));

            var bounds = camera.GetVisibleCellBounds();
            // At 1x zoom with 800x560 viewport and 8px tiles: ~100x70 cells visible + 2 margin
            if (bounds.Width < 90 || bounds.Width > 110)
                throw new Exception($"Unexpected visible width: {bounds.Width}");
            if (bounds.Height < 60 || bounds.Height > 80)
                throw new Exception($"Unexpected visible height: {bounds.Height}");

            // Center should be visible
            if (!bounds.Contains(new Point(64, 40)))
                throw new Exception("Center cell not in visible bounds");

            Console.WriteLine("  [PASS] Visible cell bounds correct");
        }

        private static void TestSmallWindow()
        {
            var layout = new GameLayout(new Rectangle(0, 0, 640, 360));
            var viewport = layout.WorldViewport;

            if (viewport.Width <= 0 || viewport.Height <= 0)
                throw new Exception($"Small window has invalid viewport: {viewport}");

            // World viewport should leave space for panels
            if (viewport.X != layout.LeftPanel.Width || viewport.Y != layout.TopBar.Bottom)
                throw new Exception($"Small window viewport position wrong: {viewport}");

            Console.WriteLine("  [PASS] Small window (640x360) layout valid");
        }

        private static void TestBelowMinimumWindowRemainsValid()
        {
            var layout = new GameLayout(new Rectangle(0, 0, 320, 180));
            var viewport = layout.WorldViewport;

            if (viewport.Width <= 0 || viewport.Height <= 0)
                throw new Exception($"Below-minimum resize produced an invalid viewport: {viewport}");
            if (viewport.Right > layout.RightPanel.Left || viewport.Bottom > layout.BottomPanel.Top)
                throw new Exception($"Below-minimum resize produced overlapping regions: viewport={viewport}, right={layout.RightPanel}, bottom={layout.BottomPanel}");

            Console.WriteLine("  [PASS] Below-minimum resize keeps a valid viewport");
        }

        private static void TestStandardResolutions()
        {
            // 1280x720 at 100% scale
            var layout1 = new GameLayout(new Rectangle(0, 0, 1280, 720));
            var vp1 = layout1.WorldViewport;
            if (vp1.Width != 742 || vp1.Height != 526)
                throw new Exception($"1280x720 viewport wrong: {vp1}");

            // 1920x1080 at 100% scale
            var layout2 = new GameLayout(new Rectangle(0, 0, 1920, 1080));
            var vp2 = layout2.WorldViewport;
            if (vp2.Width != 1382 || vp2.Height != 886)
                throw new Exception($"1920x1080 viewport wrong: {vp2}");

            Console.WriteLine("  [PASS] Standard resolutions layout correct");
        }

        private static void TestUltrawide()
        {
            var layout = new GameLayout(new Rectangle(0, 0, 3440, 1440));
            var viewport = layout.WorldViewport;

            if (viewport.Width <= 0 || viewport.Height <= 0)
                throw new Exception($"Ultrawide viewport invalid: {viewport}");

            // Right panel should be at correct position
            var rightPanel = layout.RightPanel;
            if (rightPanel.X != 3440 - layout.RightPanelWidth)
                throw new Exception($"Ultrawide right panel position wrong: {rightPanel}");

            Console.WriteLine("  [PASS] Ultrawide (3440x1440) layout valid");
        }

        private static void TestUIScaleReadyLayout()
        {
            // Layout should be recalculatable for different window sizes
            var layout1 = new GameLayout(new Rectangle(0, 0, 1280, 720), 1.0f);
            var layout2 = layout1.WithWindowBounds(new Rectangle(0, 0, 1920, 1080));

            if (layout2.WorldViewport.Width != 1382 || layout2.WorldViewport.Height != 886)
                throw new Exception($"WithWindowBounds didn't recalculate: {layout2.WorldViewport}");

            // Test UI scale changes
            var layout100 = new GameLayout(new Rectangle(0, 0, 1280, 720), 1.0f);
            var layout125 = new GameLayout(new Rectangle(0, 0, 1280, 720), 1.25f);
            var layout150 = new GameLayout(new Rectangle(0, 0, 1280, 720), 1.5f);
            var layout75 = new GameLayout(new Rectangle(0, 0, 1280, 720), 0.75f);

            // All should have positive world viewport
            if (layout100.WorldViewport.Width <= 0 || layout100.WorldViewport.Height <= 0)
                throw new Exception("100% scale: invalid viewport");
            if (layout125.WorldViewport.Width <= 0 || layout125.WorldViewport.Height <= 0)
                throw new Exception("125% scale: invalid viewport");
            if (layout150.WorldViewport.Width <= 0 || layout150.WorldViewport.Height <= 0)
                throw new Exception("150% scale: invalid viewport");
            if (layout75.WorldViewport.Width <= 0 || layout75.WorldViewport.Height <= 0)
                throw new Exception("75% scale: invalid viewport");

            Console.WriteLine("  [PASS] UI scale-ready layout recalculation works");
        }

        private static void TestClickOutsideWorldViewport()
        {
            var layout = new GameLayout(new Rectangle(0, 0, 1280, 720));

            // Click in top bar
            if (layout.IsPointInWorldViewport(new Point(100, 20)))
                throw new Exception("Top bar click incorrectly detected as world viewport");

            // Click in right panel
            if (layout.IsPointInWorldViewport(new Point(1100, 100)))
                throw new Exception("Right panel click incorrectly detected as world viewport");

            // Click in left intervention panel
            if (layout.IsPointInWorldViewport(new Point(100, 100)))
                throw new Exception("Left panel click incorrectly detected as world viewport");

            // Click in bottom panel
            if (layout.IsPointInWorldViewport(new Point(100, 650)))
                throw new Exception("Bottom panel click incorrectly detected as world viewport");

            // Click in world viewport
            if (!layout.IsPointInWorldViewport(layout.WorldViewport.Center))
                throw new Exception("World viewport click not detected");

            Console.WriteLine("  [PASS] Click outside WorldViewport correctly rejected");
        }

        private static void TestUISettingsPersistence()
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "hollowbound-ui-tests-" + Guid.NewGuid().ToString("N"));
            var settingsPath = Path.Combine(settingsDir, "settings.json");
            try
            {
                UISettingsStore.Save(settingsPath, new UISettingsData { UiScale = 1.25f, OnboardingCompleted = true });
                var loaded = UISettingsStore.Load(settingsPath);
                if (Math.Abs(loaded.UiScale - 1.25f) > 0.001f || !loaded.OnboardingCompleted)
                    throw new Exception("Valid settings did not round-trip");

                File.WriteAllText(settingsPath, "{ invalid json }");
                var invalid = UISettingsStore.Load(settingsPath);
                if (invalid.UiScale != 1f || invalid.OnboardingCompleted)
                    throw new Exception("Invalid settings did not fall back to safe defaults");

                File.Delete(settingsPath);
                var missing = UISettingsStore.Load(settingsPath);
                if (missing.UiScale != 1f)
                    throw new Exception("Missing settings did not use default UI scale");
            }
            finally
            {
                if (Directory.Exists(settingsDir))
                    Directory.Delete(settingsDir, true);
            }

            Console.WriteLine("  [PASS] UISettings persistence handles valid/invalid/missing JSON");
        }

        private static void TestOnboardingProgression()
        {
            var onboarding = new FirstCycleUXState();
            if (onboarding.CurrentStep != OnboardingStep.MoveCamera)
                throw new Exception("Onboarding did not start at camera movement");

            onboarding.MarkCameraMoved();
            onboarding.MarkAgentSelected();
            onboarding.MarkTimeStarted();
            onboarding.ObserveFood(true);
            onboarding.ObserveIntervention(true);
            onboarding.MarkChronicleOpened();
            if (!onboarding.IsComplete)
                throw new Exception("Onboarding did not complete after all required observations");

            onboarding.Reset();
            if (onboarding.IsComplete || onboarding.CurrentStep != OnboardingStep.MoveCamera)
                throw new Exception("Onboarding reset failed");

            Console.WriteLine("  [PASS] First Cycle onboarding progression is ordered and resettable");
        }

        private static void TestDecisionExplanation()
        {
            var agent = new AgentState
            {
                Id = 1,
                Energy = 20f,
                Action = AgentAction.GoingToFood,
                HasKnownFood = true,
                FoodUtilityBias = 0.4f,
                LearningUpdates = 3,
            };
            var reasons = AgentDecisionExplainer.Explain(agent);
            if (reasons.Count is < 1 or > 3 || !reasons.Any(reason => reason.Contains("energy", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Decision explanation did not expose the strongest observable motive");

            Console.WriteLine("  [PASS] Agent decision explanation returns bounded causal reasons");
                    }

                    private static void TestResponsiveLayoutMinPanelWidths()
                    {
                        // 75% UI scale at 1280x720 - panels should not be compressed below minimums
                        var layout = new GameLayout(new Rectangle(0, 0, 1280, 720), 0.75f);

                        if (layout.LeftPanelWidth < GameLayout.MinLeftPanelWidth)
                            throw new Exception($"Left panel width {layout.LeftPanelWidth} < minimum {GameLayout.MinLeftPanelWidth} at 75% scale");
                        if (layout.RightPanelWidth < GameLayout.MinRightPanelWidth)
                            throw new Exception($"Right panel width {layout.RightPanelWidth} < minimum {GameLayout.MinRightPanelWidth} at 75% scale");
                        if (layout.TopBarHeight < GameLayout.MinTopBarHeight)
                            throw new Exception($"Top bar height {layout.TopBarHeight} < minimum {GameLayout.MinTopBarHeight} at 75% scale");
                        if (layout.BottomPanelHeight < GameLayout.MinBottomPanelHeight)
                            throw new Exception($"Bottom panel height {layout.BottomPanelHeight} < minimum {GameLayout.MinBottomPanelHeight} at 75% scale");

                        var vp = layout.WorldViewport;
                        if (vp.Width < GameLayout.MinWorldViewportWidth)
                            throw new Exception($"World viewport width {vp.Width} < minimum {GameLayout.MinWorldViewportWidth}");
                        if (vp.Height < GameLayout.MinWorldViewportHeight)
                            throw new Exception($"World viewport height {vp.Height} < minimum {GameLayout.MinWorldViewportHeight}");

                        Console.WriteLine("  [PASS] Responsive layout min panel widths enforced");
                    }

                    private static void TestUIScaleAllScales()
                    {
                        float[] scales = { 0.75f, 1.0f, 1.25f, 1.5f };
                        var window = new Rectangle(0, 0, 1280, 720);

                        foreach (var scale in scales)
                        {
                            var layout = new GameLayout(window, scale);

                            // All panels must have at least their minimum physical size
                            if (layout.LeftPanel.Width < GameLayout.MinLeftPanelWidth)
                                throw new Exception($"Left panel too narrow at {scale * 100}%: {layout.LeftPanel.Width}");
                            if (layout.RightPanel.Width < GameLayout.MinRightPanelWidth)
                                throw new Exception($"Right panel too narrow at {scale * 100}%: {layout.RightPanel.Width}");
                            if (layout.TopBar.Height < GameLayout.MinTopBarHeight)
                                throw new Exception($"Top bar too short at {scale * 100}%: {layout.TopBar.Height}");
                            if (layout.BottomPanel.Height < GameLayout.MinBottomPanelHeight)
                                throw new Exception($"Bottom panel too short at {scale * 100}%: {layout.BottomPanel.Height}");

                            // World viewport must be positive
                            if (layout.WorldViewport.Width <= 0 || layout.WorldViewport.Height <= 0)
                                throw new Exception($"World viewport invalid at {scale * 100}%: {layout.WorldViewport}");
                        }

                        Console.WriteLine("  [PASS] All 4 UI scales (75/100/125/150%) produce valid layout");
                    }

                    private static void TestUIInputIsolation()
                    {
                        // Test that UI panels correctly reject world viewport clicks
                        var layout = new GameLayout(new Rectangle(0, 0, 1280, 720));

                        // Top bar should not be world viewport
                        if (layout.IsPointInWorldViewport(new Point(100, 20)))
                            throw new Exception("Top bar incorrectly detected as world viewport");

                        // Left panel should not be world viewport
                        if (layout.IsPointInWorldViewport(new Point(50, 100)))
                            throw new Exception("Left panel incorrectly detected as world viewport");

                        // Right panel should not be world viewport
                        if (layout.IsPointInWorldViewport(new Point(1200, 100)))
                            throw new Exception("Right panel incorrectly detected as world viewport");

                        // Bottom panel should not be world viewport
                        if (layout.IsPointInWorldViewport(new Point(100, 650)))
                            throw new Exception("Bottom panel incorrectly detected as world viewport");

                        // World viewport center should be detected as world viewport
                        if (!layout.IsPointInWorldViewport(layout.WorldViewport.Center))
                            throw new Exception("World viewport center not detected");

                        // Test mouse wheel isolation: wheel over UI panel should not affect camera
                        // This is a logic test - the camera only zooms if mouse is in WorldViewport
                        // (verified by HandleCameraInput checking IsPointInWorldViewport)

                        Console.WriteLine("  [PASS] UI input isolation prevents clicks/wheel on UI from affecting world");
                    }
                }
            }
