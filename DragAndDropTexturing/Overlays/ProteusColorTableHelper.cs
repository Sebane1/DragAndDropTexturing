using FFXIVLooseTextureCompiler.ImageProcessing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DragAndDropTexturing.Overlays
{
    public static class ProteusColorTableHelper
    {
        public static string GetOverlayKey(ResolvedAdvancedOverlay overlay)
        {
            if (overlay == null)
                return null;

            return !string.IsNullOrEmpty(overlay.DiffusePath) ? overlay.DiffusePath
                : (!string.IsNullOrEmpty(overlay.NormalPath) ? overlay.NormalPath
                : (!string.IsNullOrEmpty(overlay.MaskPath) ? overlay.MaskPath : overlay.IndexPath));
        }

        public static Dictionary<int, ProteusOverlayCompositor.ColorTableRow> ToCompositorRows(
            IList<AdvancedColorTableRow> rows)
        {
            if (rows == null || rows.Count == 0)
                return new Dictionary<int, ProteusOverlayCompositor.ColorTableRow>();

            var mapped = new List<(int Row, ProteusOverlayCompositor.ColorTableSubRow? SubRowA, ProteusOverlayCompositor.ColorTableSubRow? SubRowB)>();
            foreach (var row in rows)
            {
                mapped.Add((row.Row, ToSubRow(row.SubRowA), ToSubRow(row.SubRowB)));
            }

            return ProteusOverlayCompositor.BuildRowDictionary(mapped);
        }

        public static IList<AdvancedColorTableRow> GetEffectiveRows(
            IList<AdvancedColorTableRow> metadataRows,
            IList<AdvancedColorTableRow> userRows)
        {
            if (userRows != null && userRows.Count > 0)
                return userRows;

            if (metadataRows != null && metadataRows.Count > 0)
                return metadataRows;

            return new List<AdvancedColorTableRow> { CreateDefaultRow(16) };
        }

        public static List<AdvancedColorTableRow> CreateEditableRowSet(IList<AdvancedColorTableRow> metadataRows)
        {
            var byRow = new Dictionary<int, AdvancedColorTableRow>();
            if (metadataRows != null)
            {
                foreach (var row in metadataRows)
                {
                    if (row.Row >= 1 && row.Row <= 16)
                        byRow[row.Row] = CloneRow(row);
                }
            }

            var result = new List<AdvancedColorTableRow>();
            for (int i = 1; i <= 16; i++)
            {
                result.Add(byRow.TryGetValue(i, out var existing) ? existing : CreateDefaultRow(i));
            }

            return result;
        }

        public static AdvancedColorTableRow CreateDefaultRow(int rowNumber)
        {
            return new AdvancedColorTableRow
            {
                Row = rowNumber,
                SubRowA = CreateDefaultSubRow(),
                SubRowB = CreateDefaultSubRow(),
            };
        }

        public static AdvancedColorSubRow CreateDefaultSubRow()
        {
            return new AdvancedColorSubRow
            {
                Diffuse = "#FFFFFF",
                Emissive = 0f,
                Opacity = 0,
            };
        }

        public static AdvancedColorTableRow CloneRow(AdvancedColorTableRow row)
        {
            if (row == null)
                return CreateDefaultRow(16);

            return new AdvancedColorTableRow
            {
                Row = row.Row,
                SubRowA = CloneSubRow(row.SubRowA),
                SubRowB = CloneSubRow(row.SubRowB),
            };
        }

        public static AdvancedColorSubRow CloneSubRow(AdvancedColorSubRow sub)
        {
            if (sub == null)
                return CreateDefaultSubRow();

            return new AdvancedColorSubRow
            {
                Diffuse = sub.Diffuse,
                Emissive = sub.Emissive,
                Opacity = sub.Opacity,
            };
        }

        public static Vector4 SubRowToTint(AdvancedColorSubRow sub)
        {
            var (r, g, b) = ProteusOverlayCompositor.ParseHexColor(sub?.Diffuse);
            return new Vector4(r, g, b, 1f);
        }

        public static void TintToSubRowDiffuse(ref AdvancedColorSubRow sub, Vector4 tint)
        {
            sub ??= CreateDefaultSubRow();
            int ri = (int)Math.Clamp(tint.X * 255f, 0, 255);
            int gi = (int)Math.Clamp(tint.Y * 255f, 0, 255);
            int bi = (int)Math.Clamp(tint.Z * 255f, 0, 255);
            sub.Diffuse = $"#{ri:X2}{gi:X2}{bi:X2}";
        }

        public static bool RowIsAuthoredInMetadata(int rowNumber, IList<AdvancedColorTableRow> metadataRows)
        {
            return metadataRows != null && metadataRows.Any(r => r.Row == rowNumber);
        }

        private static ProteusOverlayCompositor.ColorTableSubRow? ToSubRow(AdvancedColorSubRow sub)
        {
            if (sub == null)
                return null;

            var (r, g, b) = ProteusOverlayCompositor.ParseHexColor(sub.Diffuse);
            return new ProteusOverlayCompositor.ColorTableSubRow
            {
                DiffuseR = r,
                DiffuseG = g,
                DiffuseB = b,
                Emissive = sub.Emissive,
                Opacity = sub.Opacity,
            };
        }

        public static Vector4 Row16DefaultTint(IList<AdvancedColorTableRow> rows)
        {
            var dict = ToCompositorRows(rows);
            var sub = ProteusOverlayCompositor.ResolveFlatSubRow(dict);
            return new Vector4(sub.DiffuseR, sub.DiffuseG, sub.DiffuseB, 1f);
        }
    }
}
