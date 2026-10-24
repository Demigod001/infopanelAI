using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace InfoPanel.Views.Components
{
    /// <summary>
    /// Interaction logic for CommonProperties.xaml
    /// </summary>
    public partial class CommonProperties : UserControl
    {

        public CommonProperties()
        {
            InitializeComponent();
        }

        private void ButtonUp_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.Y -= SharedModel.Instance.MoveValue;
            //}

            Services.EditorActions.MoveSelected(SharedModel.Instance.SelectedItems, 0, -SharedModel.Instance.MoveValue);
        }

        private void ButtonDown_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.Y += SharedModel.Instance.MoveValue;
            //}

            Services.EditorActions.MoveSelected(SharedModel.Instance.SelectedItems, 0, SharedModel.Instance.MoveValue);
        }

        private void ButtonLeft_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.X -= SharedModel.Instance.MoveValue;
            //}

            Services.EditorActions.MoveSelected(SharedModel.Instance.SelectedItems, -SharedModel.Instance.MoveValue, 0);
        }

        private void ButtonRight_Click(object sender, RoutedEventArgs e)
        {
            //if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
            //{
            //    displayItem.X += SharedModel.Instance.MoveValue;
            //}

            Services.EditorActions.MoveSelected(SharedModel.Instance.SelectedItems, SharedModel.Instance.MoveValue, 0);


        }

        private void ButtonMoveValue_Click(object sender, RoutedEventArgs e)
        {
            switch (SharedModel.Instance.MoveValue)
            {
                case 1:
                    SharedModel.Instance.MoveValue = 5;
                    break;
                case 5:
                    SharedModel.Instance.MoveValue = 10;
                    break;
                case 10:
                    SharedModel.Instance.MoveValue = 20;
                    break;
                case 20:
                    SharedModel.Instance.MoveValue = 1;
                    break;
                default:
                    SharedModel.Instance.MoveValue = 1;
                    break;
            }
        }

        private void NumberBoxX_TextChanged(object sender, TextChangedEventArgs e)
        {
            var numBox = ((NumberBox)sender);
            double newValue;
            if (double.TryParse(numBox.Text, out newValue))
            {
                numBox.Value = newValue;
                if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
                {
                    // With several items selected, the box shows the first item's position and changing it
                    // moves the whole selection by the same amount (it used to move only the first item).
                    var dx = (int)newValue - displayItem.X;
                    if (dx != 0)
                    {
                        if (SharedModel.Instance.IsMultipleItemsSelected)
                            Services.EditorActions.MoveSelected(SharedModel.Instance.SelectedItems.Prepend(displayItem).Distinct(), dx, 0);
                        else
                            displayItem.X = (int)newValue; // single item: exact position, as before (even if locked)
                    }
                }
            }
         }

        private void NumberBoxY_TextChanged(object sender, TextChangedEventArgs e)
        {
            var numBox = ((NumberBox)sender);
            double newValue;
            if (double.TryParse(numBox.Text, out newValue))
            {
                numBox.Value = newValue;
                if (SharedModel.Instance.SelectedItem is DisplayItem displayItem)
                {
                    var dy = (int)newValue - displayItem.Y;
                    if (dy != 0)
                    {
                        if (SharedModel.Instance.IsMultipleItemsSelected)
                            Services.EditorActions.MoveSelected(SharedModel.Instance.SelectedItems.Prepend(displayItem).Distinct(), 0, dy);
                        else
                            displayItem.Y = (int)newValue; // single item: exact position, as before (even if locked)
                    }
                }
            }
        }

        private void ButtonStep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var step))
            {
                SharedModel.Instance.MoveValue = step;
            }
        }

        private void ButtonAlign_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<Utils.AlignMode>(tag, out var mode))
            {
                Services.EditorActions.AlignSelected(mode);
            }
        }

        private void ButtonDistribute_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag })
            {
                Services.EditorActions.DistributeSelected(horizontal: tag == "Horizontal");
            }
        }
    }
}
