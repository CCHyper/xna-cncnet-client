using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClientCore;
using DTAClient.Domain.Multiplayer.CnCNet;
using ClientCore.Extensions;

using ClientGUI;
using ClientUpdater;
using DTAClient.Domain.Multiplayer;
using DTAClient.DXGUI.Multiplayer.CnCNet;
using DTAClient.Online;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xna.Framework;
using Rampastring.Tools;
using Rampastring.XNAUI;

namespace DTAClient.DXGUI.Generic
{
    public class LoadingScreen : XNAWindow
    {
        public LoadingScreen(
            CnCNetManager cncnetManager,
            WindowManager windowManager,
            IServiceProvider serviceProvider,
            MapLoader mapLoader,
            Random random
        ) : base(windowManager)
        {
            this.cncnetManager = cncnetManager;
            this.serviceProvider = serviceProvider;
            this.mapLoader = mapLoader;
            this.random = random;
        }

        private static readonly object locker = new object();

        private MapLoader mapLoader;

        private Random random;

        private PrivateMessagingPanel privateMessagingPanel;

        private bool visibleSpriteCursor;

        private Task updaterInitTask;
        private Task mapLoadTask;
        private readonly CnCNetManager cncnetManager;
        private readonly IServiceProvider serviceProvider;

        private List<string> randomTextures;

        // Set when a PreLoadingScreen is active; defers Finish() until it completes.
        private bool _preMoviePlaying;

        // Guards Finish() so it only runs once.
        private bool _finished;

        // Minimum seconds to display loading screen after pre-movie finishes.
        private double _fakeDelaySeconds;
        private bool _hadPreMovie;
        private double _loadingShownTime;

        public override void Initialize()
        {
            ClientRectangle = new Rectangle(0, 0, 800, 600);
            Name = "LoadingScreen";
            BackgroundTexture = AssetLoader.LoadTexture("loadingscreen.png");

            base.Initialize();

            CenterOnParent();

            bool initUpdater = !ClientConfiguration.Instance.ModMode;

            if (initUpdater)
            {
                updaterInitTask = new Task(InitUpdater);
                updaterInitTask.Start();
            }

            mapLoadTask = mapLoader.LoadMapsAsync();

            if (Cursor.Visible)
            {
                Cursor.Visible = false;
                visibleSpriteCursor = true;
            }

            // Show PreLoadingScreen movie on top if its INI exists.
            if (MovieScreenIniExists("PreLoadingScreen"))
            {
                _preMoviePlaying = true;
                Visible = false;

                var preScreen = new MovieScreen(WindowManager) { Name = "PreLoadingScreen" };
                preScreen.Completed += (_, _) =>
                {
                    WindowManager.RemoveControl(preScreen);
                    _preMoviePlaying = false;
                    _hadPreMovie = true;
                    _loadingShownTime = 0;
                };
                WindowManager.AddAndInitializeControl(preScreen);
            }
        }

        protected override void GetINIAttributes(IniFile iniFile)
        {
            base.GetINIAttributes(iniFile);

            _fakeDelaySeconds = iniFile.GetDoubleValue(Name, "FakeDelaySeconds", 0);

            randomTextures = iniFile.GetStringListValue(Name, "RandomBackgroundTextures", string.Empty).ToList();

            if (randomTextures.Count == 0)
                return;

            BackgroundTexture = AssetLoader.LoadTexture(randomTextures[random.Next(randomTextures.Count)]);
        }

        private void InitUpdater()
        {
            Updater.OnLocalFileVersionsChecked += LogGameClientVersion;
            Updater.CheckLocalFileVersions();
        }

        private void LogGameClientVersion()
        {
            Logger.Log($"Game Client Version: {ClientConfiguration.Instance.LocalGame} {Updater.GameVersion}");
            Updater.OnLocalFileVersionsChecked -= LogGameClientVersion;
        }

        private void Finish()
        {
            _finished = true;

            ProgramConstants.GAME_VERSION = ClientConfiguration.Instance.ModMode ?
                "N/A" : Updater.GameVersion;

            bool postExists = MovieScreenIniExists("PostLoadingScreen");
            Logger.Log($"LoadingScreen.Finish: PostLoadingScreen INI exists = {postExists}");

            if (postExists)
            {
                // Hide LoadingScreen behind the movie.
                Visible = false;

                var postScreen = new MovieScreen(WindowManager) { Name = "PostLoadingScreen" };
                postScreen.Completed += (_, _) =>
                {
                    // Initialize MainMenu before removing the movie to avoid a black frame.
                    ShowMainMenu();
                    WindowManager.RemoveControl(postScreen);
                };
                WindowManager.AddAndInitializeControl(postScreen);
            }
            else
            {
                ShowMainMenu();
            }
        }

        private void ShowMainMenu()
        {
            var mainMenu = serviceProvider.GetRequiredService<MainMenu>();
            WindowManager.AddAndInitializeControl(mainMenu);

            if (UserINISettings.Instance.AutomaticCnCNetLogin &&
                NameValidator.IsNameValid(ProgramConstants.PLAYERNAME) == null)
            {
                cncnetManager.Connect();
            }

            if (!UserINISettings.Instance.PrivacyPolicyAccepted)
            {
                WindowManager.AddAndInitializeControl(new PrivacyNotification(WindowManager));
            }

            WindowManager.RemoveControl(this);

            Cursor.Visible = visibleSpriteCursor;
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            // Don't finish while the pre-movie is still playing.
            if (_preMoviePlaying)
                return;

            if (_finished)
                return;

            // After pre-movie, ensure loading screen is visible and track display time.
            if (_hadPreMovie)
                _loadingShownTime += gameTime.ElapsedGameTime.TotalSeconds;

            bool loadingDone = (updaterInitTask == null || updaterInitTask.Status == TaskStatus.RanToCompletion)
                && mapLoadTask.Status == TaskStatus.RanToCompletion;

            bool minTimeElapsed = !_hadPreMovie || _loadingShownTime >= _fakeDelaySeconds;

            if (loadingDone && minTimeElapsed)
            {
                Finish();
                return;
            }

            // Still waiting — show loading screen if hidden (e.g., after pre-movie).
            if (!Visible)
                Visible = true;
        }

        /// <summary>
        /// Checks whether a MovieScreen INI file exists in any resource path.
        /// </summary>
        private static bool MovieScreenIniExists(string screenName)
        {
            string fileName = $"{screenName}.ini";
            return SafePath.GetFile(ProgramConstants.GetResourcePath(), fileName).Exists
                || SafePath.GetFile(ProgramConstants.GetBaseResourcePath(), fileName).Exists;
        }
    }
}
