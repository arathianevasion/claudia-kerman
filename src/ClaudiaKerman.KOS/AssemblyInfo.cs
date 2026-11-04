// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

// KSP refuses to load this assembly (and logs why) unless these are installed.
// 0Harmony.dll declares no KSPAssembly; the HarmonyKSP mod registers as "HarmonyKSP" 1.0.
[assembly: KSPAssembly("ClaudiaKerman.KOS", 0, 1, 0)]
[assembly: KSPAssemblyDependency("ClaudiaKerman", 0, 1)]
[assembly: KSPAssemblyDependency("kOS", 1, 6)]
[assembly: KSPAssemblyDependency("HarmonyKSP", 1, 0)]
