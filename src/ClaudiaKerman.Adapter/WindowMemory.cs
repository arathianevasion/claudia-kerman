// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using ClaudiaKerman.Core.Ui;

namespace ClaudiaKerman.Adapter
{
    /// <summary>
    /// Remembers where each window sat in each scene, in the mod's config folder (not the save). The logic
    /// is <see cref="WindowLayouts"/>; this only supplies the scene and the folder.
    /// </summary>
    public static class WindowMemory
    {
        private static WindowLayouts _layouts = new WindowLayouts();
        private static string _directory;

        public static void Init(string directory)
        {
            _directory = directory;
            _layouts = WindowLayouts.Load(directory);
        }

        /// <summary>The current scene's key: the Space Center, the VAB and SPH separately, Flight.</summary>
        public static string Scene()
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.FLIGHT: return "flight";
                case GameScenes.SPACECENTER: return "spacecenter";
                case GameScenes.EDITOR:
                    return EditorDriver.editorFacility == EditorFacility.SPH ? "sph" : "vab";
                default: return "other";
            }
        }

        /// <summary>The box resized from the UI scale it was kept at to <paramref name="scaleNow"/>.</summary>
        public static bool TryGet(string window, float scaleNow, out WindowBox box)
        {
            return _layouts.TryGet(Scene(), window, scaleNow, out box);
        }

        /// <summary>Keeps the box with the UI <paramref name="scale"/> its size is for.</summary>
        public static void Store(string window, WindowBox box, float scale)
        {
            if (_directory != null && _layouts.Set(Scene(), window, box, scale)) _layouts.Save(_directory);
        }

        /// <summary>Forgets where the window sat in this scene, so it goes back to its default.</summary>
        public static void Forget(string window)
        {
            if (_directory != null && _layouts.Remove(Scene(), window)) _layouts.Save(_directory);
        }
    }
}
