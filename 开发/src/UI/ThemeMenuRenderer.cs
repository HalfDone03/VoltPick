using System.Drawing;
using System.Windows.Forms;

namespace DeviceBattery
{
    internal sealed class ThemeMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Background; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Border; } }
        public override Color MenuItemSelected { get { return Theme.TabSelected; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.TabSelected; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.TabSelected; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.TabSelected; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.TabSelected; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Background; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Background; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Background; } }
        public override Color SeparatorDark { get { return Theme.Divider; } }
        public override Color SeparatorLight { get { return Theme.Divider; } }
        public override Color ButtonSelectedHighlight { get { return Theme.TabSelected; } }
        public override Color ButtonSelectedBorder { get { return Theme.Border; } }
    }

    internal static class ThemeMenu
    {
        public static void Apply(ContextMenuStrip menu)
        {
            if (menu == null)
            {
                return;
            }
            menu.BackColor = Theme.Background;
            menu.ForeColor = Theme.Text;
            menu.RenderMode = ToolStripRenderMode.Professional;
            menu.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColorTable());
            for (int i = 0; i < menu.Items.Count; i++)
            {
                ApplyItem(menu.Items[i]);
            }
        }

        private static void ApplyItem(ToolStripItem item)
        {
            item.BackColor = Theme.Background;
            item.ForeColor = Theme.Text;
            ToolStripMenuItem menuItem = item as ToolStripMenuItem;
            if (menuItem == null || menuItem.DropDown == null)
            {
                return;
            }
            menuItem.DropDown.BackColor = Theme.Background;
            menuItem.DropDown.ForeColor = Theme.Text;
            menuItem.DropDown.RenderMode = ToolStripRenderMode.Professional;
            menuItem.DropDown.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColorTable());
            for (int i = 0; i < menuItem.DropDownItems.Count; i++)
            {
                ApplyItem(menuItem.DropDownItems[i]);
            }
        }
    }
}
