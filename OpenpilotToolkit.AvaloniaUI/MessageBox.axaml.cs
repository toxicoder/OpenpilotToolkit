using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace OpenpilotToolkit.AvaloniaUI
{
    public partial class MessageBox : Window
    {
        public enum MessageBoxButtons
        {
            Ok,
            OkCancel
        }

        public enum MessageBoxResult
        {
            Ok,
            Cancel
        }

        public MessageBox()
        {
            InitializeComponent();
            OkButton.Click += (sender, e) => Close(MessageBoxResult.Ok);
            CancelButton.Click += (sender, e) => Close(MessageBoxResult.Cancel);
        }

        public static async Task<MessageBoxResult> Show(Window parent, string text, string title, MessageBoxButtons buttons)
        {
            var msgbox = new MessageBox
            {
                Title = title
            };
            msgbox.FindControl<TextBlock>("MessageTextBlock").Text = text;
            var buttonPanel = msgbox.FindControl<StackPanel>("ButtonPanel");

            var res = MessageBoxResult.Ok;

            void AddButton(string caption, MessageBoxResult r, bool def = false)
            {
                var btn = new Button { Content = caption };
                btn.Click += (_, __) => {
                    res = r;
                    msgbox.Close();
                };
                buttonPanel.Children.Add(btn);
                if (def)
                    res = r;
            }

            if (buttons == MessageBoxButtons.Ok || buttons == MessageBoxButtons.OkCancel)
                AddButton("Ok", MessageBoxResult.Ok, true);
            if (buttons == MessageBoxButtons.OkCancel)
                AddButton("Cancel", MessageBoxResult.Cancel, false);

            await msgbox.ShowDialog(parent);
            return res;
        }
    }
}
