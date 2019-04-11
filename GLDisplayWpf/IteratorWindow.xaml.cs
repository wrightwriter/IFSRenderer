﻿using System;
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
using System.Windows.Shapes;

namespace GLDisplayWpf
{
    /// <summary>
    /// Interaction logic for IteratorWindow.xaml
    /// </summary>
    public partial class IteratorWindow : Window
    {
        public IteratorWindow(EventHandler eh)
        {
            InitializeComponent();
            view.Refresh_CallBack += eh;
        }
    }
}
