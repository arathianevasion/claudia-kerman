// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Andrew Watson

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ClaudiaKermin.Core.Kos;
using Xunit;

namespace ClaudiaKermin.Core.Tests
{
    /// <summary>
    /// Guardrail 1 of ADR 0014: a Building Block's documented interface (its <c>//|</c> lines, which is what
    /// <see cref="CoreLibrary.FullDocs"/> gives Claudia) is pinned by one golden file per block, so an accidental change fails the gate.
    /// </summary>
    public class BlockDocsSnapshotTests
    {
        private static string Normal(string text)
        {
            return text.Replace("\r\n", "\n").TrimEnd('\n');
        }

        private static string SourceDir([CallerFilePath] string here = "")
        {
            return Path.Combine(Path.GetDirectoryName(here), "Fixtures", "block-docs");
        }

        private static string GoldenDir()
        {
            return Path.Combine(AppContext.BaseDirectory, "Fixtures", "block-docs");
        }

        [Fact]
        public void EveryBlocksDocsMatchItsSnapshot()
        {
            // To rewrite the snapshots after a deliberate interface change: create an empty Fixtures/block-docs/UPDATE, run this test, delete UPDATE.
            if (File.Exists(Path.Combine(SourceDir(), "UPDATE")))
            {
                Directory.CreateDirectory(SourceDir());
                foreach (var old in Directory.GetFiles(SourceDir(), "*.txt")) File.Delete(old);
                foreach (var block in CoreLibrary.Blocks)
                    File.WriteAllText(Path.Combine(SourceDir(), block.Name + ".txt"), Normal(CoreLibrary.FullDocs(new[] { block.Name })) + "\n");
                return;
            }

            var bad = CoreLibrary.Blocks
                .Select(b => new { b.Name, Path = Path.Combine(GoldenDir(), b.Name + ".txt"), Docs = Normal(CoreLibrary.FullDocs(new[] { b.Name })) })
                .Where(b => !File.Exists(b.Path) || Normal(File.ReadAllText(b.Path)) != b.Docs)
                .Select(b => "Block '" + b.Name + "': its //| doc lines differ from Fixtures/block-docs/" + b.Name + ".txt. Update the snapshot only for a deliberate interface change, and say so in the commit message (ADR 0014).")
                .ToList();
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        [Fact]
        public void EverySnapshotHasABlock()
        {
            var names = CoreLibrary.Blocks.Select(b => b.Name).ToList();
            var stale = Directory.GetFiles(GoldenDir(), "*.txt").Select(Path.GetFileNameWithoutExtension).Where(n => !names.Contains(n)).ToList();
            Assert.True(stale.Count == 0, "Snapshot without a block: " + string.Join(", ", stale) + ". Delete it if the block was removed on purpose.");
        }
    }
}
