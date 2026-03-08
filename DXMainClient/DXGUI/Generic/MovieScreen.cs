using System;
using System.Collections.Generic;
using System.Linq;

using ClientGUI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Rampastring.Tools;
using Rampastring.XNAUI;
using Rampastring.XNAUI.XNAControls;

namespace DTAClient.DXGUI.Generic
{
    /// <summary>
    /// A reusable full-screen window that plays one or more FFMpegVideoPlayer
    /// children defined via INI ExtraControls. Skippable with Escape or Spacebar.
    ///
    /// Usage: set <see cref="XNAControl.Name"/> before initialization so the
    /// framework resolves the correct INI file (e.g. "PreLoadingScreen" loads
    /// PreLoadingScreen.ini).
    /// </summary>
    public class MovieScreen : XNAWindow
    {
        public event EventHandler Completed;

        private List<FFMpegVideoPlayer> _players;
        private int _completedCount;
        private bool _finished;
        private bool _completedFired;
        private bool _cursorWasVisible;

        public MovieScreen(WindowManager windowManager)
            : base(windowManager)
        {
        }

        public override void Initialize()
        {
            ClientRectangle = new Rectangle(0, 0, WindowManager.WindowWidth, WindowManager.WindowHeight);

            base.Initialize();

            // Set after base.Initialize() so INI/GenericWindow defaults don't override.
            PanelBackgroundDrawMode = PanelBackgroundImageDrawMode.STRETCHED;
            BackgroundTexture = AssetLoader.CreateTexture(Color.Black, 1, 1);
            DrawBorders = false;

            // Discover FFMpegVideoPlayer children created by INI [ExtraControls].
            _players = Children.OfType<FFMpegVideoPlayer>().ToList();

            if (_players.Count == 0)
            {
                Logger.Log($"MovieScreen '{Name}': No video players defined, skipping.");
                _finished = true;
                return;
            }

            foreach (var player in _players)
            {
                // Force Pause so the last frame stays visible until this screen is removed.
                // Remove (the default) calls Stop() which sets _closing = true, preventing Draw().
                player.OnEnd = FFMpegVideoPlayer.EndAction.Pause;
                player.Completed += OnPlayerCompleted;
            }

            if (Cursor.Visible)
            {
                _cursorWasVisible = true;
                Cursor.Visible = false;
            }
        }

        public override void Update(GameTime gameTime)
        {
            if (_finished)
            {
                FireCompleted();
                return;
            }

            if (Keyboard.IsKeyHeldDown(Keys.Escape) || Keyboard.IsKeyHeldDown(Keys.Space))
            {
                StopAllAndFinish();
                return;
            }

            base.Update(gameTime);
        }

        public override void Draw(GameTime gameTime)
        {
            // Keep drawing until actually removed from WindowManager to avoid black flash.
            base.Draw(gameTime);
        }

        private void OnPlayerCompleted()
        {
            _completedCount++;

            if (_completedCount >= _players.Count)
            {
                _finished = true;
            }
        }

        private void StopAllAndFinish()
        {
            foreach (var player in _players)
            {
                try
                {
                    player.Completed -= OnPlayerCompleted;
                    player.Stop();
                }
                catch
                {
                    // Swallow errors during skip teardown.
                }
            }

            _finished = true;
        }

        private void FireCompleted()
        {
            if (_completedFired)
                return;

            _completedFired = true;

            if (_cursorWasVisible)
                Cursor.Visible = true;

            Completed?.Invoke(this, EventArgs.Empty);
        }
    }
}
