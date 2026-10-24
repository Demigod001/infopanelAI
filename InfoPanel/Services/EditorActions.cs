using InfoPanel.Models;
using InfoPanel.Utils;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Services
{
    /// <summary>
    /// Editing actions that work on the whole selection: moving, aligning, distributing and box
    /// selection. The item lock means "prevent drag move", so arrow moves still apply to locked items
    /// (as before), while alignment, like dragging, leaves them where they are.
    /// </summary>
    public static class EditorActions
    {
        public static void MoveSelected(IEnumerable<DisplayItem> items, int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            foreach (var item in items)
            {
                item.X += dx;
                item.Y += dy;
            }
        }

        /// <summary>Selected items that alignment can act on: visible, unlocked, not group containers.</summary>
        private static List<DisplayItem> AlignableSelection() =>
            SharedModel.Instance.SelectedVisibleItems
                .Where(item => item is not GroupDisplayItem && !item.IsLocked)
                .ToList();

        public static int AlignableCount => AlignableSelection().Count;

        public static void AlignSelected(AlignMode mode)
        {
            var items = AlignableSelection();
            Apply(items, LayoutMath.Align(Bounds(items), mode));
        }

        public static void DistributeSelected(bool horizontal)
        {
            var items = AlignableSelection();
            Apply(items, LayoutMath.Distribute(Bounds(items), horizontal));
        }

        /// <summary>
        /// Select every visible item in <paramref name="profile"/> whose bounds touch <paramref name="box"/>.
        /// With <paramref name="additive"/>, existing selections are kept. A null box with no
        /// <paramref name="additive"/> just clears the selection.
        /// </summary>
        public static void SelectInBox(Profile profile, ItemRect? box, bool additive)
        {
            var model = SharedModel.Instance;
            if (model.SelectedProfile != profile) return;

            foreach (var item in model.GetProfileDisplayItemsCopy(profile))
            {
                if (item is GroupDisplayItem group)
                {
                    foreach (var child in group.DisplayItemsCopy)
                        ApplySelection(child);
                    if (!additive && group.Selected) group.Selected = false;
                }
                else
                {
                    ApplySelection(item);
                }
            }

            model.NotifySelectedItemChange();

            void ApplySelection(DisplayItem item)
            {
                var hit = box is ItemRect b && !item.Hidden && ToRect(item).IntersectsWith(b);
                if (hit) item.Selected = true;
                else if (!additive) item.Selected = false;
            }
        }

        private static ItemRect ToRect(DisplayItem item)
        {
            var b = item.EvaluateBounds();
            return new ItemRect(b.Left, b.Top, b.Right, b.Bottom);
        }

        private static List<ItemRect> Bounds(List<DisplayItem> items) => items.Select(ToRect).ToList();

        private static void Apply(List<DisplayItem> items, (int Dx, int Dy)[] moves)
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].X += moves[i].Dx;
                items[i].Y += moves[i].Dy;
            }
        }
    }
}
