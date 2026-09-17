import sys

def main():
    with open("MainWindow.xaml.bak", "r", encoding="utf-8") as f:
        content = f.read()

    # 1. Header (up to <Grid x:Name="FilesView")
    start_files_view = content.find('<Grid x:Name="FilesView" Visibility="Visible">')
    header_part = content[:start_files_view]

    # Inside FilesView
    # Ribbon
    start_ribbon = content.find('<!-- Панель команд (Floating Glass Ribbon Dock) -->')
    end_ribbon = content.find('<!-- Строка пути и Поиск -->')
    ribbon_part = content[start_ribbon:end_ribbon]

    # Path Bar
    start_path = end_ribbon
    end_path = content.find('<!-- Статус-бар внизу -->')
    path_part = content[start_path:end_path]

    # Status bar
    start_status = end_path
    end_status = content.find('<!-- Основное тело (Splitter:')
    status_part = content[start_status:end_status]

    # Sidebar (Left panel navigation)
    start_sidebar = content.find('<!-- Левая панель навигации: Быстрый доступ и Накопители -->')
    end_sidebar = content.find('<!-- Разделитель колонок -->')
    sidebar_part = content[start_sidebar:end_sidebar]

    # File Browser List (Right panel)
    start_browser = content.find('<!-- Правая панель: Файловый браузер -->')
    end_browser = content.find('</Grid>', start_browser)
    # The browser is inside a Grid and DockPanel.
    end_files_view = content.find('</Grid>', end_browser + 7) # end of FilesView Grid
    
    file_browser_part = content[start_browser:content.find('</Grid>', content.find('</ListView>', start_browser))]
    
    # Other Views
    start_transfer_view = content.find('<!-- ================= 2. ДВИЖОК ПЕРЕДАЧ')
    end_all_views = content.find('</Grid>\n        </DockPanel>')
    other_views_part = content[start_transfer_view:end_all_views]

    # Construct New XAML
    
    # Modify Header: Replace Ribbon Tabs with Window Title, and adjust sizes
    # Replace top <DockPanel> with <Grid>
    new_header = header_part.replace('<DockPanel>', '''<Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" /> <!-- Header -->
                <RowDefinition Height="Auto" /> <!-- Toolbar & Path -->
                <RowDefinition Height="*" /> <!-- Main Area -->
                <RowDefinition Height="Auto" /> <!-- Footer -->
            </Grid.RowDefinitions>''')
            
    # Fix the Grid DockPanel.Dock="Top" -> Grid.Row="0"
    new_header = new_header.replace('DockPanel.Dock="Top" Height="46"', 'Grid.Row="0" Height="56"')
    
    # Remove the tabs from header
    start_tabs = new_header.find('<!-- Вкладки верхнего уровня (Футуристический док) -->')
    end_tabs = new_header.find('<!-- Быстрые переключатели: Окно настроек и тема -->')
    tabs_part = new_header[start_tabs:end_tabs]
    
    # We will move tabs_part to the Sidebar. So we delete it from Header.
    new_header = new_header[:start_tabs] + new_header[end_tabs:]

    # Constructing Toolbar (Ribbon + Path) in Row 1
    toolbar = f'''
            <!-- TOOLBAR -->
            <StackPanel Grid.Row="1">
{ribbon_part.replace('DockPanel.Dock="Top"', '')}
{path_part.replace('DockPanel.Dock="Top"', '')}
            </StackPanel>
'''

    # Constructing Main Area in Row 2
    main_area = f'''
            <!-- MAIN AREA -->
            <Grid Grid.Row="2">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="260" MinWidth="200"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*" MinWidth="350"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="300" MinWidth="200" MaxWidth="500"/>
                </Grid.ColumnDefinitions>

                <!-- SIDEBAR -->
                <Border Grid.Column="0" Background="{{DynamicResource SidebarBackgroundBrush}}">
                    <StackPanel>
                        <!-- NAV TABS MOVED HERE -->
                        <TextBlock Text="НАВИГАЦИЯ" FontSize="11" FontWeight="Bold" Style="{{StaticResource MutedText}}" Margin="16,12,0,4"/>
                        <StackPanel Margin="10,0">
                            <RadioButton x:Name="TabFilesRadio" Style="{{StaticResource FuturisticNavTab}}" Content="Файлы" GroupName="TopNav" IsChecked="True" Click="NavTab_Click" Checked="NavTab_Checked" PreviewMouseLeftButtonDown="NavTab_PreviewMouseDown"/>
                            <RadioButton x:Name="TabTransferRadio" Style="{{StaticResource FuturisticNavTab}}" Content="Передача" GroupName="TopNav" Click="NavTab_Click" Checked="NavTab_Checked" PreviewMouseLeftButtonDown="NavTab_PreviewMouseDown"/>
                            <RadioButton x:Name="TabStorageRadio" Style="{{StaticResource FuturisticNavTab}}" Content="Накопители" GroupName="TopNav" Click="NavTab_Click" Checked="NavTab_Checked" PreviewMouseLeftButtonDown="NavTab_PreviewMouseDown"/>
                            <RadioButton x:Name="TabDiagnosticsRadio" Style="{{StaticResource FuturisticNavTab}}" Content="Диагностика" GroupName="TopNav" Click="NavTab_Click" Checked="NavTab_Checked" PreviewMouseLeftButtonDown="NavTab_PreviewMouseDown"/>
                            <RadioButton x:Name="TabToolsRadio" Style="{{StaticResource FuturisticNavTab}}" Content="Утилиты" GroupName="TopNav" Click="NavTab_Click" Checked="NavTab_Checked" PreviewMouseLeftButtonDown="NavTab_PreviewMouseDown"/>
                        </StackPanel>
                        
{sidebar_part}
                    </StackPanel>
                </Border>
                
                <GridSplitter Grid.Column="1" Width="1" HorizontalAlignment="Center" Background="{{DynamicResource GlassBorderBrush}}"/>

                <!-- WORKSPACE -->
                <Grid Grid.Column="2" Margin="10">
                    <Grid x:Name="FilesView" Visibility="Visible">
{file_browser_part}
                    </Grid>
{other_views_part}
                <!-- end of workspace grid, no closing tag needed because other_views_part doesn't close the parent grid? wait. -->
'''

    # We need to be careful: other_views_part ends with the last View (ToolsView).
    main_area_end = '''
                </Grid>
                
                <GridSplitter Grid.Column="3" Width="1" HorizontalAlignment="Center" Background="{{DynamicResource GlassBorderBrush}}"/>

                <!-- INSPECTOR -->
                <Border Grid.Column="4" Background="{{DynamicResource SidebarBackgroundBrush}}">
                    <StackPanel Margin="16">
                        <TextBlock Text="СВОЙСТВА" FontSize="11" FontWeight="Bold" Style="{{StaticResource MutedText}}" Margin="0,0,0,10"/>
                        <Border Style="{{StaticResource Card}}" Height="150" Margin="0,0,0,10">
                            <TextBlock Text="Выберите файл для просмотра" HorizontalAlignment="Center" VerticalAlignment="Center" Style="{{StaticResource MutedText}}"/>
                        </Border>
                        <TextBlock Text="MOTION AI" FontSize="11" FontWeight="Bold" Style="{{StaticResource MutedText}}" Margin="0,0,0,10"/>
                        <Border Style="{{StaticResource Card}}" Height="100">
                            <TextBlock Text="Анализ недоступен" HorizontalAlignment="Center" VerticalAlignment="Center" Style="{{StaticResource MutedText}}"/>
                        </Border>
                    </StackPanel>
                </Border>
            </Grid>
'''

    footer = f'''
            <!-- FOOTER -->
            <Border Grid.Row="3" Background="{{DynamicResource RibbonBackgroundBrush}}" BorderBrush="{{DynamicResource GlassBorderBrush}}" BorderThickness="0,1,0,0">
{status_part.replace('DockPanel.Dock="Bottom"', '')}
            </Border>
        </Grid>
    </Border>
</Window>
'''

    final_xaml = new_header + toolbar + main_area + main_area_end + footer

    with open("MainWindow.xaml", "w", encoding="utf-8") as f:
        f.write(final_xaml)
        
    print("XAML successfully generated.")

if __name__ == "__main__":
    main()
