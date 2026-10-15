using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuota.Core.Models;
using CodexQuota.Core.Services;
using CodexQuota.Desktop.ViewModels;
using CodexQuota.Desktop.Views;

namespace CodexQuota.Desktop.Services;

/// <summary>
/// Builds the real windows with clearly-labeled synthetic data to verify desktop
/// construction, bindings, geometry, color states and interaction wiring, and to
/// produce rendered screenshots. It never contacts a provider, never touches local
/// records, and the synthetic data is unreachable from the normal runtime path.
/// </summary>
internal sealed class VerificationHarness
{
    private const string SampleBanner = "验证示例数据 SAMPLE (not live)";
    private const double BallSize = 72;
    private const double PanelWidth = 360;

    private readonly WidgetViewModel _widget;
    private readonly SettingsViewModel _settings;
    private readonly string _executablePath;
    private readonly ThemeService _themeService;
    private readonly List<Dictionary<string, object?>> _checks = [];
    private readonly List<string> _screenshots = [];

    public VerificationHarness(WidgetViewModel widget, SettingsViewModel settings, string executablePath, ThemeService themeService)
    {
        _widget = widget;
        _settings = settings;
        _executablePath = executablePath;
        _themeService = themeService;
    }

    public async Task RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        VerifyFiveHourProjection();

        _widget.Update(FreshSampleState());
        _widget.UpdateLocalUsage(LocalAvailable());
        _widget.UpdateObservedUsage(ObservedAvailable());

        Check("ball_text_includes_percent_sign", "46%", _widget.BallText);
        Check("ball_reports_selected_fivehour_remaining", 46d, _widget.BallRemainingPercent);
        Check("ball_has_value_when_fresh", true, _widget.BallHasValue);
        Check("ball_period_label_is_actual_fivehour_period", "5h", _widget.BallPeriodText);
        Check("ball_severity_yellow_at_46", QuotaSeverity.Yellow, _widget.Severity);
        Check("tooltip_has_no_severity_adjective", true, !HasSeverityAdjective(_widget.TooltipText));
        Check("fivehour_and_weekly_cards_visible", 2, _widget.PeriodCards.Count);
        Check("weekly_card_has_own_remaining_and_duration", true, _widget.PeriodCards[1].Title == "周额度" && _widget.PeriodCards[1].RemainingPercent == 92 && _widget.PeriodCards[1].PeriodText == "7 天");
        Check("period_card_title_preserves_selected_slot", "主额度", _widget.PeriodCards[0].Title);
        Check("period_card_shows_actual_period", "5 小时", _widget.PeriodCards[0].PeriodText);
        Check("period_card_remaining_has_no_adjective", true, !HasSeverityAdjective(_widget.PeriodCards[0].RemainingText));
        Check("nonfivehour_auxiliary_rows_hidden", 0, _widget.AuxiliaryRows.Count);
        Check("plan_present", true, _widget.PlanText.Contains("plus", StringComparison.Ordinal));

        Check("local_sessions_visible", "3 个对话", _widget.LocalSessionsText);
        Check("local_tokens_formatted", "123,456 Tokens", _widget.LocalTokensText);
        Check("local_badge_is_non_official", "非官方", _widget.LocalBadgeText);
        Check("local_complete_card_has_no_coverage_paragraph", string.Empty, _widget.LocalCoverageText);
        Check("local_info_explains_no_quota_conversion", true, _widget.LocalInfoText.Contains("不会换算成额度百分比", StringComparison.Ordinal));
        _widget.UpdateLocalUsage(LocalAvailable() with { Date = DateOnly.FromDateTime(DateTime.Now).AddDays(-1) });
        Check("yesterday_local_totals_are_hidden_at_rollover", false, _widget.HasLocalNumbers);
        Check("rollover_pending_message_describes_today", true, _widget.LocalPendingText.Contains("今天", StringComparison.Ordinal));
        _widget.UpdateLocalUsage(LocalAvailable());

        var scheduler = new ManualDelayScheduler();
        using var controller = new InteractionController(scheduler);

        var ball = new FloatingBallWindow { DataContext = _widget };
        ball.AttachController(controller);
        ball.ApplyOpacity(0.95);
        Layout(ball, BallSize, BallSize);

        Check("ball_width_72", BallSize, ball.Width);
        Check("ball_height_72", BallSize, ball.Height);
        Check("ball_topmost", true, ball.Topmost);
        Check("ball_hidden_from_taskbar", false, ball.ShowInTaskbar);
        Check("ball_allows_transparency", true, ball.AllowsTransparency);
        Check("ball_transparent_background", null, ball.Background);
        Check("floating_restores_large_percentage_name_and_real_period", true,
            FindVisuals<System.Windows.Controls.TextBlock>(ball).Any(block => block.Text == "46%" && block.FontSize >= 15) &&
            ContainsVisualText(ball, "Codex") && ContainsVisualText(ball, "5h"));
        Check("floating_has_no_external_badge_pixel", 0, (int)SampleWidgetPixel(ball, 70, 60).A);
        Check("non_badge_widget_corners_are_transparent", true, WidgetCornersAreTransparent(ball));
        Check("floating_inline_quota_point_matches_remaining_tier", true, FloatingHealthMatches(ball, "QuotaAmberTextBrush"));
        using (System.Drawing.Bitmap tray = AppIcon.RenderBitmap(_widget.IconState, 32))
        {
            Check("non_badge_tray_corners_are_transparent", true, tray.GetPixel(4, 4).A == 0 && tray.GetPixel(28, 4).A == 0 && tray.GetPixel(4, 28).A == 0);
            System.Drawing.Color badge = tray.GetPixel(31, 26);
            Check("reference_tray_badge_has_white_protruding_separator", true, IsWhiteBadgePixel(Color.FromArgb(badge.A, badge.R, badge.G, badge.B)));
        }
        VerifyStableArcGradient();
        var ballRing = FindVisual<ProgressRing>(ball);
        Check("ball_ring_present", true, ballRing is not null);
        Check("ball_ring_value_matches_remaining", 46d, ballRing?.Value);
        Check("ball_ring_draws_partial_arc", true, ballRing?.HasValue == true);
        Check("rendered_floating_ring_is_yellow_at_46", true, RenderedColorMatches("QuotaAmberTextBrush", SampleRingTopColor(ball)));

        RenderScreenshot(ball, Path.Combine(outputDirectory, "ball-light-46.png"), Brushes.WhiteSmoke);

        var details = new DetailsWindow { DataContext = _widget };
        Layout(details, PanelWidth, double.PositiveInfinity);
        Check("details_panel_width", PanelWidth, Math.Round(details.PanelWidth));
        Check("details_settings_gear_present", true, details.FindName("SettingsGear") is System.Windows.Controls.Button);
        Check("details_close_button_present", true, details.FindName("CloseButton") is System.Windows.Controls.Button);
        Check("details_has_no_severity_adjectives", true, !HasSeverityAdjective(CollectVisualText(details)));
        Check("details_shows_no_technical_main_bucket_label", false, CollectVisualText(details).Contains("主桶 codex", StringComparison.Ordinal));
        Check("details_render_both_actual_period_titles", true, ContainsVisualText(details, "主额度（5 小时）") && ContainsVisualText(details, "周额度（7 天）"));
        Check("remaining_label_is_explicit", true, ContainsVisualText(details, "剩余") && ContainsVisualText(details, "46%"));
        Check("available_daily_card_has_no_scope_paragraph", false, CollectVisualText(details).Contains("本机记录（含 agent 会话）", StringComparison.Ordinal));
        Check("reference_panel_is_compact", 360d, details.PanelWidth);
        Check("reference_daily_shows_only_primary_estimate", 1, _widget.ObservedPeriods.Count);
        Check("reference_daily_estimate_bound_to_primary", "约3.5%", (details.FindName("DailyEstimateValue") as System.Windows.Controls.TextBlock)?.Text);
        Check("reference_daily_has_no_tokens", false, CollectVisualText(details).Contains("Tokens", StringComparison.Ordinal));
        Check("reference_panel_has_no_credits", false, CollectVisualText(details).Contains("积分余额", StringComparison.Ordinal));
        Check("reference_tracks_are_shorter", true, FindVisuals<System.Windows.Controls.ProgressBar>(details).All(bar => bar.ActualWidth <= 286));
        Check("reference_ball_dot_follows_remaining_tier", true, BrushMatches("QuotaAmberTextBrush", _widget.BallDotBrush));

        bool panelShown = false;
        bool settingsRequested = false;
        details.CloseRequested += () => controller.CloseExplicitly();
        details.SettingsRequested += () => settingsRequested = true;
        controller.PanelShown += () =>
        {
            panelShown = true;
            details.Show();
        };
        controller.PanelHidden += () =>
        {
            panelShown = false;
            details.Hide();
        };

        controller.PointerEnteredBall();
        scheduler.FireAll();
        Check("hover_opens_unpinned_panel", true, panelShown && !controller.IsPinned);
        Check("panel_visible_after_hover", true, details.IsVisible);
        await FlushResourceChangesAsync(details);
        CheckDetailsHintPadding(details, "initial_details_content_fit");

        controller.PointerExitedBall();
        scheduler.FireAll();
        Check("unpinned_panel_closes_after_grace", false, panelShown);

        controller.ShowPinned();
        Check("tray_details_opens_pinned", true, panelShown && controller.IsPinned);

        // The header gear raises the same settings request the ball context menu uses.
        ((System.Windows.Controls.Button)details.FindName("SettingsGear")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("details_gear_raises_settings_request", true, settingsRequested);

        controller.CloseExplicitly();
        Check("explicit_close_hides_panel", false, panelShown);

        controller.BallClicked();
        Check("click_pins_panel", true, panelShown && controller.IsPinned);
        controller.BallClicked();
        Check("second_click_closes_pinned_panel", false, panelShown);

        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-fresh.png"), Brushes.WhiteSmoke);
        Check("period_header_remaining_is_right_aligned", true, HeaderMatchesReference(details));

        _widget.UpdateLocalUsage(LocalPartial());
        details.ShowActivated = false;
        details.Show();
        await FlushResourceChangesAsync(details);
        Check("local_partial_coverage_is_labeled", true, CollectVisualText(details).Contains("部分统计", StringComparison.Ordinal));
        Check("partial_local_render_has_no_severity_adjective", true, !HasSeverityAdjective(CollectVisualText(details)));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-local-partial.png"), Brushes.WhiteSmoke);

        // Long local numbers and a long reset date must not overlap or clip the card.
        _widget.UpdateLocalUsage(LocalLongValues());
        await FlushResourceChangesAsync(details);
        Check("long_local_values_render", "128 个对话", (details.FindName("DailySessionValue") as System.Windows.Controls.TextBlock)?.Text);
        Check("long_local_render_has_no_severity_adjective", true, !HasSeverityAdjective(CollectVisualText(details)));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-long-values.png"), Brushes.WhiteSmoke);
        details.Hide();

        _widget.UpdateLocalUsage(LocalUnavailable());
        details.ShowActivated = false;
        details.Show();
        await FlushResourceChangesAsync(details);
        Check("local_unavailable_is_not_a_fabricated_zero", true, ContainsVisualText(details, "本机对话统计暂不可用") && _widget.LocalSessionsNumber == "—");
        Check("unavailable_local_render_has_no_severity_adjective", true, !HasSeverityAdjective(CollectVisualText(details)));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-local-unavailable.png"), Brushes.WhiteSmoke);
        details.Hide();
        _widget.UpdateLocalUsage(LocalAvailable());

        // The observed-consumption estimate lives in the same daily card but stays
        // independent of the local Token numbers: waiting, known-zero/available and a
        // partial long lower bound, each rendered in both themes.
        _widget.UpdateObservedUsage(ObservedWaiting());
        Check("observed_waiting_row_present", 1, _widget.ObservedPeriods.Count);
        Check("observed_waiting_row_waits", "等待第二次可比观测", _widget.ObservedPeriods[0].ValueText);
        Check("observed_waiting_message_visible", true, _widget.HasObservedPending);
        Check("observed_waiting_has_no_number", false, _widget.ObservedHasNumber);
        details.ShowActivated = false;
        details.Show();
        await FlushResourceChangesAsync(details);
        Check("observed_waiting_renders_title", true, ContainsVisualText(details, "估算消耗") && ContainsVisualText(details, "等待观测"));
        Check("observed_waiting_render_has_no_severity_adjective", true, !HasSeverityAdjective(CollectVisualText(details)));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-observed-waiting.png"), Brushes.WhiteSmoke);
        details.Hide();

        _widget.UpdateObservedUsage(ObservedAvailable());
        Check("observed_available_primary_value", "约3.5%", _widget.ObservedPeriods[0].ValueText);
        Check("observed_available_secondary_is_not_displayed", 1, _widget.ObservedPeriods.Count);
        Check("observed_available_has_number", true, _widget.ObservedHasNumber);
        Check("observed_available_has_no_note", false, _widget.HasObservedNote);
        details.ShowActivated = false;
        details.Show();
        await FlushResourceChangesAsync(details);
        Check("observed_available_render_shows_value", true, ContainsVisualText(details, "约3.5%"));
        Check("observed_tooltip_explains_full_budget_percentage", true,
            _widget.ObservedInfoText.Contains("10% → 15%", StringComparison.Ordinal) &&
            _widget.ObservedInfoText.Contains("5%", StringComparison.Ordinal) &&
            _widget.ObservedInfoText.Contains("50%", StringComparison.Ordinal));
        Check("observed_available_keeps_local_numbers", "3 个对话", (details.FindName("DailySessionValue") as System.Windows.Controls.TextBlock)?.Text);
        Check("observed_available_render_has_no_severity_adjective", true, !HasSeverityAdjective(CollectVisualText(details)));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-observed-available.png"), Brushes.WhiteSmoke);
        details.Hide();

        _widget.UpdateObservedUsage(ObservedAvailable() with { LocalDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-1) });
        Check("yesterday_observed_rows_hidden_at_rollover", false, _widget.HasObservedPeriods);
        Check("yesterday_observed_numbers_hidden_at_rollover", false, _widget.ObservedHasNumber);
        Check("yesterday_observed_waits_for_today", true, _widget.HasObservedPending);
        _widget.UpdateObservedUsage(ObservedAvailable());
        _widget.UpdateLocalUsage(LocalUnavailable());
        Check("observed_numbers_independent_of_unavailable_local_tokens", true, _widget.ObservedHasNumber && !_widget.HasLocalNumbers);
        _widget.UpdateLocalUsage(LocalAvailable());

        _widget.UpdateObservedUsage(ObservedPartialLong());
        Check("observed_partial_long_value", "约148.5%（部分）", _widget.ObservedPeriods[0].ValueText);
        Check("observed_partial_secondary_is_not_displayed", 1, _widget.ObservedPeriods.Count);
        Check("observed_partial_note_visible", true, _widget.HasObservedNote);
        details.ShowActivated = false;
        details.Show();
        await FlushResourceChangesAsync(details);
        Check("observed_partial_render_shows_long_value", true, ContainsVisualText(details, "约148.5%（部分）"));
        Check("observed_partial_render_has_no_severity_adjective", true, !HasSeverityAdjective(CollectVisualText(details)));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-observed-partial.png"), Brushes.WhiteSmoke);
        details.Hide();

        _widget.UpdateObservedUsage(ObservedUsageSnapshot.Initial(DateOnly.FromDateTime(DateTime.Now)));
        Check("observed_initial_has_no_numbers", false, _widget.ObservedHasNumber);
        Check("observed_initial_pending_visible", true, _widget.HasObservedPending);
        _widget.UpdateObservedUsage(ObservedAvailable());

        _widget.Update(BuildState(usedPercent: 70, status: RefreshStatus.Fresh)); // remaining 30 -> red
        Check("severity_red_at_30", QuotaSeverity.Red, _widget.Severity);
        var redAt30Ball = new FloatingBallWindow { DataContext = _widget };
        Layout(redAt30Ball, BallSize, BallSize);
        Check("rendered_floating_ring_is_red_at_30", true, RenderedColorMatches("SeverityRedBrush", SampleRingTopColor(redAt30Ball)));
        RenderScreenshot(redAt30Ball, Path.Combine(outputDirectory, "ball-light-red-30.png"), Brushes.WhiteSmoke);
        redAt30Ball.Close();

        _widget.Update(BuildState(usedPercent: 69, status: RefreshStatus.Fresh)); // remaining 31 -> red
        Check("severity_red_at_31", QuotaSeverity.Red, _widget.Severity);

        _widget.Update(BuildState(usedPercent: 85, status: RefreshStatus.Fresh)); // remaining 15 -> red
        Check("severity_red_at_15", QuotaSeverity.Red, _widget.Severity);

        _widget.Update(BuildState(usedPercent: 66, status: RefreshStatus.Fresh)); // remaining 34 -> yellow
        Check("severity_yellow_at_34", QuotaSeverity.Yellow, _widget.Severity);
        var yellowBall = new FloatingBallWindow { DataContext = _widget };
        Layout(yellowBall, BallSize, BallSize);
        Check("rendered_floating_ring_is_yellow_at_34", true, RenderedColorMatches("QuotaAmberTextBrush", SampleRingTopColor(yellowBall)));
        RenderScreenshot(yellowBall, Path.Combine(outputDirectory, "ball-light-yellow-34.png"), Brushes.WhiteSmoke);
        yellowBall.Close();

        _widget.Update(BuildState(usedPercent: 50, status: RefreshStatus.Fresh)); // remaining 50 -> yellow
        Check("severity_yellow_at_50", QuotaSeverity.Yellow, _widget.Severity);

        _widget.Update(BuildState(usedPercent: 34, status: RefreshStatus.Fresh)); // remaining 66 -> yellow
        Check("severity_yellow_at_66", QuotaSeverity.Yellow, _widget.Severity);

        _widget.Update(BuildState(usedPercent: 33, status: RefreshStatus.Fresh)); // remaining 67 -> green
        Check("severity_green_at_67", QuotaSeverity.Green, _widget.Severity);
        var greenAt67Ball = new FloatingBallWindow { DataContext = _widget };
        Layout(greenAt67Ball, BallSize, BallSize);
        Check("rendered_floating_ring_is_green_at_67", true, RenderedColorMatches("SeverityGreenBrush", SampleRingTopColor(greenAt67Ball)));
        RenderScreenshot(greenAt67Ball, Path.Combine(outputDirectory, "ball-light-green-67.png"), Brushes.WhiteSmoke);
        greenAt67Ball.Close();

        _widget.Update(BuildState(usedPercent: 86, status: RefreshStatus.Fresh)); // remaining 14 -> red
        Check("severity_red_at_14", QuotaSeverity.Red, _widget.Severity);
        var redBall14 = new FloatingBallWindow { DataContext = _widget };
        Layout(redBall14, BallSize, BallSize);
        Check("rendered_floating_ring_is_red_at_14", true, RenderedColorMatches("SeverityRedBrush", SampleRingTopColor(redBall14)));
        RenderScreenshot(redBall14, Path.Combine(outputDirectory, "ball-light-red-14.png"), Brushes.WhiteSmoke);
        redBall14.Close();

        _widget.Update(BuildState(usedPercent: 30, status: RefreshStatus.Fresh)); // remaining 70 -> green
        Check("severity_green_at_70", QuotaSeverity.Green, _widget.Severity);
        var greenAt70Ball = new FloatingBallWindow { DataContext = _widget };
        Layout(greenAt70Ball, BallSize, BallSize);
        Check("rendered_floating_ring_is_green_at_70", true, RenderedColorMatches("SeverityGreenBrush", SampleRingTopColor(greenAt70Ball)));
        RenderScreenshot(greenAt70Ball, Path.Combine(outputDirectory, "ball-light-green-70.png"), Brushes.WhiteSmoke);
        greenAt70Ball.Close();

        _widget.Update(BuildState(usedPercent: 29, status: RefreshStatus.Fresh)); // remaining 71 -> green
        Check("severity_green_at_71", QuotaSeverity.Green, _widget.Severity);
        var greenBall = new FloatingBallWindow { DataContext = _widget };
        Layout(greenBall, BallSize, BallSize);
        Check("rendered_floating_ring_is_green_at_71", true, RenderedColorMatches("SeverityGreenBrush", SampleRingTopColor(greenBall)));
        RenderScreenshot(greenBall, Path.Combine(outputDirectory, "ball-light-codex-71.png"), Brushes.WhiteSmoke);
        greenBall.Close();

        _widget.Update(ZeroSampleState());
        Check("zero_remaining_shows_zero_percent", "0%", _widget.BallText);
        Check("zero_remaining_number_is_red_tier", true, BrushMatches("SeverityRedBrush", _widget.BallTextBrush));
        var zeroBall = new FloatingBallWindow { DataContext = _widget };
        Layout(zeroBall, BallSize, BallSize);
        var zeroRing = FindVisual<ProgressRing>(zeroBall);
        Check("zero_remaining_draws_no_arc", true, zeroRing is not null && zeroRing.HasValue && zeroRing.Value == 0d);
        RenderScreenshot(zeroBall, Path.Combine(outputDirectory, "ball-light-zero.png"), Brushes.WhiteSmoke);
        zeroBall.Close();

        _widget.Update(FullSampleState());
        Check("full_remaining_shows_hundred_percent", "100%", _widget.BallText);
        var fullBall = new FloatingBallWindow { DataContext = _widget };
        Layout(fullBall, BallSize, BallSize);
        Check("full_remaining_draws_full_ring", 100d, FindVisual<ProgressRing>(fullBall)?.Value);
        RenderScreenshot(fullBall, Path.Combine(outputDirectory, "ball-light-full.png"), Brushes.WhiteSmoke);
        fullBall.Close();

        _widget.Update(UnknownSampleState());
        Check("unknown_state_is_neutral", QuotaSeverity.Unknown, _widget.Severity);
        Check("unknown_state_shows_dashes", "--", _widget.BallText);
        var unknownBall = new FloatingBallWindow { DataContext = _widget };
        Layout(unknownBall, BallSize, BallSize);
        Check("unknown_state_draws_no_arc", false, FindVisual<ProgressRing>(unknownBall)?.HasValue == true);
        RenderScreenshot(unknownBall, Path.Combine(outputDirectory, "ball-light-unknown.png"), Brushes.WhiteSmoke);
        unknownBall.Close();

        _widget.Update(StaleSampleState());
        Check("stale_state_is_neutral", QuotaSeverity.Stale, _widget.Severity);
        details.ShowActivated = false;
        details.Show();
        await FlushResourceChangesAsync(details);
        Check("stale_render_shows_expired_status", true, ContainsVisualText(details, "连接状态：数据已过期"));
        Check("stale_render_retains_last_known_percentage", true, CollectVisualText(details).Contains("46%", StringComparison.Ordinal));
        Check("stale_tooltip_has_no_severity_adjective", true, !HasSeverityAdjective(_widget.TooltipText));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-light-stale.png"), Brushes.WhiteSmoke);
        details.Hide();

        _widget.Update(FreshSampleState());

        _settings.LoadFrom(
            new WidgetSettings(1, 0.95, ThemeMode.System, 120, 1600, 900, null),
            _executablePath,
            "已找到官方 Codex 客户端（验证）");
        var settingsWindow = new SettingsWindow { DataContext = _settings };
        settingsWindow.ShowActivated = false;
        settingsWindow.Show();
        Layout(settingsWindow, settingsWindow.Width, double.PositiveInfinity);
        Check("settings_constructs_and_binds", true, settingsWindow.DataContext is SettingsViewModel);
        Check("settings_opacity_percent", 95d, _settings.OpacityPercent);
        Check("settings_interval_seconds", 120, _settings.IntervalSeconds);
        Check("settings_has_local_reference_size_and_details_stays_compact", true, settingsWindow.Width == (double)settingsWindow.FindResource("PanelWidth") && settingsWindow.Width > details.Width && details.Width == PanelWidth && settingsWindow.Height == (double)settingsWindow.FindResource("SettingsPanelHeight") && details.Height < settingsWindow.Height);
        string settingsText = CollectVisualText(settingsWindow);
        var displayCard = (System.Windows.Controls.GroupBox)settingsWindow.FindName("DetailsDisplayCard");
        Check("settings_four_reference_groups_and_permanent_details_options", true, FindVisuals<System.Windows.Controls.GroupBox>(settingsWindow).Count() == 4 && displayCard.IsVisible && !settingsText.Contains("高级设置", StringComparison.Ordinal) && !settingsText.Contains("CLI", StringComparison.Ordinal));
        await VerifySettingsControlsAsync(settingsWindow, details, outputDirectory);
        RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, "settings-light.png"), Brushes.WhiteSmoke);

        _widget.Update(FreshSampleState());
        _themeService.Apply(ThemeMode.Dark);
        _widget.RefreshTheme();
        await FlushResourceChangesAsync(details, settingsWindow);
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-dark-fresh.png"), Brushes.Black);
        RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, "settings-dark.png"), Brushes.Black);
        var darkBall = new FloatingBallWindow { DataContext = _widget };
        Layout(darkBall, BallSize, BallSize);
        Check("rendered_floating_ring_is_dark_yellow_at_46", true, RenderedColorMatches("QuotaAmberTextBrush", SampleRingTopColor(darkBall)));
        RenderScreenshot(darkBall, Path.Combine(outputDirectory, "ball-dark-46.png"), Brushes.Black);
        darkBall.Close();

        _widget.UpdateObservedUsage(ObservedPartialLong());
        _widget.RefreshTheme();
        await FlushResourceChangesAsync(details);
        Check("observed_partial_dark_render_shows_long_value", true, ContainsVisualText(details, "约148.5%（部分）"));
        Check("observed_partial_dark_render_has_no_severity_adjective", true, !HasSeverityAdjective(CollectVisualText(details)));
        RenderScreenshot(details, Path.Combine(outputDirectory, "details-dark-observed-partial.png"), Brushes.Black);
        _widget.UpdateObservedUsage(ObservedAvailable());
        _widget.RefreshTheme();
        await FlushResourceChangesAsync(details);

        CheckPanelBackground("dark_theme_updates_existing_details_panel", details, 0x1F);
        CheckPanelBackground("dark_theme_updates_existing_settings_panel", settingsWindow, 0x1F);
        var combo = (System.Windows.Controls.ComboBox)settingsWindow.FindName("ThemeCombo");
        combo.BringIntoView();
        await FlushResourceChangesAsync(settingsWindow);
        combo?.ApplyTemplate();
        var selection = combo?.Template.FindName("SelectionSurface", combo) as System.Windows.Controls.Border;
        Check("dark_combo_rendered_surface_is_dark", true, selection?.Background is SolidColorBrush selectedBrush && selectedBrush.Color.R < 70);
        Check("dark_combo_text_contrasts_with_surface", true, combo?.Foreground is SolidColorBrush foreground && foreground.Color.R > 200);
        Check("combo_selected_value_is_chinese_label", true, FindVisual<System.Windows.Controls.TextBlock>(selection!)?.Text == "跟随系统");
        if (combo is not null)
        {
            combo.SelectedIndex = 1;
            Check("combo_selection_updates_settings_binding", ThemeMode.Light, _settings.SelectedTheme.Mode);
            combo.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(combo), Environment.TickCount, System.Windows.Input.Key.Up)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Check("combo_keyboard_up_selects_previous_option", ThemeMode.System, _settings.SelectedTheme.Mode);
            combo.IsDropDownOpen = true;
            await FlushResourceChangesAsync(settingsWindow);
            var popup = combo.Template.FindName("PART_Popup", combo) as System.Windows.Controls.Primitives.Popup;
            var dropDown = popup?.Child as System.Windows.Controls.Border;
            Check("dark_dropdown_uses_dark_surface", true, dropDown?.Background is SolidColorBrush dropBrush && dropBrush.Color.R < 70);
            if (dropDown is not null) { RenderElement(dropDown, Path.Combine(outputDirectory, "theme-dropdown-dark.png"), Brushes.Black); }
            combo.IsDropDownOpen = false;
        }

        _themeService.Apply(ThemeMode.Light);
        _widget.RefreshTheme();
        await FlushResourceChangesAsync(details, settingsWindow);
        CheckPanelBackground("light_theme_restores_details_panel", details, 0xFF);
        Check("light_combo_surface_and_text_have_contrast", true, selection?.Background is SolidColorBrush lightBrush && lightBrush.Color.R > 200 && combo?.Foreground is SolidColorBrush textBrush && textBrush.Color.R < 70);
        settingsWindow.Close();

        await VerifyPanelSizingRendersAsync(outputDirectory, details);
        await VerifyAdditionalRendersAsync(outputDirectory, details);
        RenderDockVariants(outputDirectory);
        RenderCombinedProviderStyles(outputDirectory);

        details.Close();
        ball.Close();

        string reportPath = Path.Combine(outputDirectory, "desktop-verification.json");
        var report = new
        {
            kind = "desktop-construction-and-interaction",
            buildModuleId = typeof(App).Module.ModuleVersionId.ToString(),
            note = "Synthetic sample data; no provider calls and no local record reads were made. Slider tests use real WPF InputHitTest, native Thumb/keyboard commands and scoped/restored cached-device coordinates for routed track clicks. No physical cursor movement or physical pointer/keyboard injection; not mixed-DPI/hotplug proof.",
            banner = SampleBanner,
            screenshots = _screenshots,
            checks = _checks,
            allPassed = _checks.All(check => check["passed"] is true),
        };

        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);

        Console.WriteLine($"verification report: {reportPath}");
        Console.WriteLine($"allPassed: {report.allPassed}");
        if (!report.allPassed) { throw new InvalidOperationException("Desktop render verification failed; see the report."); }
    }

    private void VerifyFiveHourProjection()
    {
        RefreshState state = BuildState(20, RefreshStatus.Fresh);
        QuotaBucket main = state.LastQuota!.Buckets[0];
        QuotaWindow selected = main.Windows[0];
        QuotaWindow week = selected with { Slot = "secondary", DurationMinutes = 10080, UsedPercent = 90, RemainingPercent = 10, ResetsAt = selected.ResetsAt!.Value.AddDays(6) };
        foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            _themeService.Apply(theme);
            _widget.Update(state with { LastQuota = state.LastQuota with { Buckets = [main with { Windows = [week, selected] }] } });
            Check($"{theme}_fivehour80_week10_all_surface_values", true,
                _widget.BallText == "80%" && _widget.BallRemainingPercent == 80 && _widget.BallRemainingFraction == .8 &&
                _widget.BallPeriodText == "5h" && _widget.IconState.RemainingPercent == 80 &&
                _widget.PeriodCards.Count == 2 && _widget.PeriodCards[0].RemainingPercent == 80 && _widget.PeriodCards[0].UsedText == "已用 20%" &&
                _widget.PeriodCards[1].RemainingPercent == 10 && _widget.PeriodCards[1].UsedText == "已用 90%" &&
                _widget.PeriodCards[1].ResetText == "重置 " + QuotaFormatting.DescribeResetTime(week.ResetsAt, TimeZoneInfo.Local) &&
                _widget.TooltipText.Contains("80%", StringComparison.Ordinal) && !_widget.TooltipText.Contains("10%", StringComparison.Ordinal));
            Check($"{theme}_fivehour80_week10_shared_remaining_color", true,
                _widget.Severity == QuotaSeverity.Green && BrushMatches("SeverityGreenBrush", _widget.BallRingBrush) &&
                BrushMatches("SeverityGreenBrush", _widget.BallTextBrush) && BrushMatches("SeverityGreenBrush", _widget.PeriodCards[0].ValueAccent) &&
                BrushMatches("SeverityRedBrush", _widget.PeriodCards[1].ValueAccent));
            _widget.Update(state with { LastQuota = state.LastQuota with { Buckets = [main with { Windows = [week] }] } });
            Check($"{theme}_missing_fivehour_never_shows_week", true, !_widget.BallHasValue && _widget.BallText == "--" &&
                _widget.BallRemainingFraction == 0 && !_widget.BallPeriodVisible && _widget.PeriodCards.Count == 1 &&
                _widget.PeriodCards[0].Title == "周额度" && _widget.PeriodCards[0].RemainingPercent == 10 && _widget.IconState.RemainingPercent is null);
            QuotaWindow secondary = selected with { Slot = "secondary", RemainingPercent = 50, UsedPercent = 50, ResetsAt = selected.ResetsAt.Value.AddHours(1) };
            _widget.Update(state with { LastQuota = state.LastQuota with { Buckets = [main with { Windows = [selected with { DurationMinutes = 1440 }, secondary] }] } });
            Check($"{theme}_secondary_fivehour_keeps_real_used_and_reset", true,
                _widget.BallText == "50%" && _widget.PeriodCards.Count == 1 && _widget.PeriodCards[0].Title == "次时段" &&
                _widget.PeriodCards[0].UsedText == "已用 50%" && _widget.PeriodCards[0].ResetText == "重置 " + QuotaFormatting.DescribeResetTime(secondary.ResetsAt, TimeZoneInfo.Local));
            Check($"{theme}_yellow_number_ring_card_share_text_color", true,
                BrushMatches("QuotaAmberTextBrush", _widget.BallTextBrush) && BrushMatches("QuotaAmberTextBrush", _widget.BallRingBrush) &&
                _widget.PeriodCards.All(card => BrushMatches("QuotaAmberTextBrush", card.ValueAccent)));
        }
        _themeService.Apply(ThemeMode.Light);
    }

    private async Task VerifyAdditionalRendersAsync(string outputDirectory, DetailsWindow details)
    {
        // Fresh remaining tiers: 0-33 red, 34-66 yellow, 67-100 green.
        (int Remaining, QuotaSeverity Severity, string Brush)[] boundaries =
        [
            (0, QuotaSeverity.Red, "SeverityRedBrush"),
            (14, QuotaSeverity.Red, "SeverityRedBrush"),
            (30, QuotaSeverity.Red, "SeverityRedBrush"),
            (33, QuotaSeverity.Red, "SeverityRedBrush"),
            (34, QuotaSeverity.Yellow, "SeverityYellowBrush"),
            (46, QuotaSeverity.Yellow, "SeverityYellowBrush"),
            (50, QuotaSeverity.Yellow, "SeverityYellowBrush"),
            (66, QuotaSeverity.Yellow, "SeverityYellowBrush"),
            (67, QuotaSeverity.Green, "SeverityGreenBrush"),
            (70, QuotaSeverity.Green, "SeverityGreenBrush"),
            (78, QuotaSeverity.Green, "SeverityGreenBrush"),
            (100, QuotaSeverity.Green, "SeverityGreenBrush"),
        ];
        foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            string label = theme.ToString().ToLowerInvariant();
            Brush background = theme == ThemeMode.Light ? Brushes.WhiteSmoke : Brushes.Black;
            _themeService.Apply(theme);
            foreach (var fixture in boundaries)
            {
                RefreshState state = BuildState(100 - fixture.Remaining, RefreshStatus.Fresh);
                QuotaBucket main = state.LastQuota!.Buckets[0];
                _widget.Update(state with
                {
                    LastQuota = state.LastQuota with
                    {
                        Buckets = [main with { Windows = [main.Windows[0], main.Windows[0] with { Slot = "secondary", DurationMinutes = 10080 }] }],
                    },
                });
                _widget.UpdateLocalUsage(LocalAvailable());
                _widget.UpdateObservedUsage(ObservedAvailable());
                details.ShowActivated = false;
                details.Show();
                await FlushResourceChangesAsync(details);
                var ball = new FloatingBallWindow { DataContext = _widget };
                Layout(ball, BallSize, BallSize);
                string name = $"{label}_remaining_{fixture.Remaining}";
                Check(name + "_ball_policy", fixture.Severity, _widget.Severity);
                Check(name + "_selected_period_card_accent", true, _widget.PeriodCards.Count == 2 && _widget.PeriodCards.All(card => BrushMatches(fixture.Brush, card.Accent)));
                Check(name + "_actual_remaining_numbers_and_bars", true,
                    FindVisuals<System.Windows.Controls.TextBlock>(details).Count(block => block.Text == $"{fixture.Remaining}%" && BrushMatches(RemainingValueThemeKey(fixture.Severity), block.Foreground)) == 2 &&
                    FindVisuals<System.Windows.Controls.ProgressBar>(details).Count(bar => bar.Value == fixture.Remaining && BrushMatches(fixture.Brush, bar.Foreground)) == 2);
                Check(name + "_consumed_numbers_stay_neutral", true,
                    FindVisuals<System.Windows.Controls.TextBlock>(details).Where(block => block.Text.StartsWith("已用 ", StringComparison.Ordinal)).All(block => BrushMatches("TextSecondaryBrush", block.Foreground)) &&
                    details.FindName("DailyEstimateValue") is System.Windows.Controls.TextBlock estimateBlock && BrushMatches("TextPrimaryBrush", estimateBlock.Foreground));
                Check(name + "_normal_connection_green_independent_of_quota", true, ConnectionDotMatches(details, "SeverityGreenBrush"));
                Check(name + "_ball_percent_text_visible_quota_cue", true,
                    FindVisuals<System.Windows.Controls.TextBlock>(ball).Any(block => block.Text == $"{fixture.Remaining}%" &&
                        BrushMatches(RemainingValueThemeKey(fixture.Severity), block.Foreground)));
                Check(name + "_rendered_ring", true, RenderedColorMatches(fixture.Remaining == 0 ? "BallRingTrackBrush" : RemainingValueThemeKey(fixture.Severity), SampleRingTopColor(ball)));
                Check(name + "_rendered_tray_icon", true, TrayIconMatches(_widget.IconState));
                Check(name + "_floating_percentage_name_and_inline_health", true,
                    ContainsVisualText(ball, "Codex") && FloatingHealthMatches(ball, RemainingValueThemeKey(fixture.Severity)) &&
                    FindVisuals<System.Windows.Controls.TextBlock>(ball).Any(block => block.Text == $"{fixture.Remaining}%" && block.FontSize >= 15));
                VerifyQuotaArcPixels(name, ball, fixture.Remaining);
                RenderTray(_widget.IconState, Path.Combine(outputDirectory, $"tray-{label}-remaining-{fixture.Remaining}-32.png"), 32);
                RenderScreenshot(ball, Path.Combine(outputDirectory, $"ball-{label}-boundary-{fixture.Remaining}.png"), background);
                RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-boundary-{fixture.Remaining}.png"), background);
                ball.Close();
            }

            foreach (var fixture in new[]
            {
                (Name: "waiting", Estimate: ObservedWaiting(), Local: LocalAvailable()),
                (Name: "available", Estimate: ObservedAvailable(), Local: LocalAvailable()),
                (Name: "zero-primary", Estimate: ObservedAvailable() with { Periods = [ObservedAvailable().Periods[0] with { ObservedPercentPoints = 0 }] }, Local: LocalAvailable()),
                (Name: "partial-long", Estimate: ObservedPartialLong(), Local: LocalLongValues()),
                (Name: "tokens-unavailable", Estimate: ObservedAvailable(), Local: LocalUnavailable()),
                (Name: "secondary-only", Estimate: ObservedAvailable() with { Periods = [ObservedAvailable().Periods[1]] }, Local: LocalAvailable()),
                (Name: "yesterday", Estimate: ObservedAvailable() with { LocalDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-1) }, Local: LocalAvailable() with { Date = DateOnly.FromDateTime(DateTime.Now).AddDays(-1) }),
                (Name: "max-count", Estimate: ObservedPartialLong(), Local: LocalLongValues() with { SessionCount = int.MaxValue }),
                (Name: "metadata-unavailable", Estimate: new ObservedUsageSnapshot(DateOnly.FromDateTime(DateTime.Now), ObservedUsageState.Unavailable,
                    [new ObservedPeriodEstimate("primary", null, ObservedPeriodState.Unavailable, 0, 0, null, null, true)]), Local: LocalAvailable()),
            })
            {
                _widget.Update(FreshSampleState());
                _widget.UpdateObservedUsage(fixture.Estimate);
                _widget.UpdateLocalUsage(fixture.Local);
                await FlushResourceChangesAsync(details);
                Check($"{label}_{fixture.Name}_observed_title_visible", true, ContainsVisualText(details, "估算消耗"));
                Check($"{label}_{fixture.Name}_long_values_fit", true, VisibleTextFits(details));
                if (fixture.Name == "zero-primary")
                {
                    Check($"{label}_primary_known_zero_is_visible", true, ContainsVisualText(details, "约0%"));
                }
                if (fixture.Name is "secondary-only" or "metadata-unavailable" or "yesterday")
                {
                    Check($"{label}_{fixture.Name}_no_fabricated_primary_number", false, _widget.ObservedHasNumber);
                    Check($"{label}_{fixture.Name}_primary_unavailable_is_visible", true, ContainsVisualText(details, "暂无估算"));
                }
                if (fixture.Name == "tokens-unavailable")
                {
                    Check($"{label}_missing_local_keeps_primary_estimate", true, ContainsVisualText(details, "约3.5%") && _widget.LocalSessionsNumber == "—");
                }
                if (fixture.Name == "yesterday")
                {
                    Check($"{label}_yesterday_count_is_hidden", "—", _widget.LocalSessionsNumber);
                }
                RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-observed-{fixture.Name}-complete.png"), background);
            }

            RefreshState reference = BuildState(76, RefreshStatus.Fresh);
            QuotaBucket referenceMain = reference.LastQuota!.Buckets[0];
            _widget.Update(reference with
            {
                LastQuota = reference.LastQuota with
                {
                    Buckets = [referenceMain with { Windows = [referenceMain.Windows[0], new QuotaWindow("secondary", 10080, 22, 78, DateTimeOffset.Now.AddDays(6))] }],
                },
            });
            _widget.UpdateLocalUsage(LocalAvailable() with { SessionCount = 12 });
            _widget.UpdateObservedUsage(ObservedAvailable() with { Periods = [ObservedAvailable().Periods[0] with { ObservedPercentPoints = 18 }] });
            await FlushResourceChangesAsync(details);
            Check($"{label}_reference_fivehour24_and_weekly78_are_visible", true,
                ContainsVisualText(details, "24%") && ContainsVisualText(details, "78%") &&
                ContainsVisualText(details, "周额度（7 天）") && _widget.BallText == "24%");
            Check($"{label}_reference_daily_estimate", true, ContainsVisualText(details, "约18%") && ContainsVisualText(details, "12 个对话"));
            Check($"{label}_reference_track_width", true, FindVisuals<System.Windows.Controls.ProgressBar>(details).All(bar => bar.ActualWidth <= 286));
            Check($"{label}_reference_numbers_fit", true, VisibleTextFits(details));
            RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-reference-24-78.png"), background);
            var referenceBall = new FloatingBallWindow { DataContext = _widget };
            Layout(referenceBall, BallSize, BallSize);
            Check($"{label}_reference_ball_red_ring", true, RenderedColorMatches("SeverityRedBrush", SampleRingTopColor(referenceBall)));
            Check($"{label}_reference_ball_dot_matches_tier", true, BrushMatches("SeverityRedBrush", _widget.BallDotBrush));
            RenderScreenshot(referenceBall, Path.Combine(outputDirectory, $"ball-{label}-reference-24.png"), background);
            referenceBall.Close();

            foreach (RefreshStatus status in new[] { RefreshStatus.Stale, RefreshStatus.Error, RefreshStatus.Loading })
            {
                _widget.Update(status == RefreshStatus.Stale ? StaleSampleState() : UnknownSampleState() with { Status = status });
                _widget.UpdateObservedUsage(ObservedUsageSnapshot.Initial(DateOnly.FromDateTime(DateTime.Now)));
                await FlushResourceChangesAsync(details);
                var ball = new FloatingBallWindow { DataContext = _widget };
                Layout(ball, BallSize, BallSize);
                Check($"{label}_{status}_neutral_ring_and_tray", true, BrushMatches("SeverityNeutralBrush", _widget.BallRingBrush) && TrayIconMatches(_widget.IconState));
                Check($"{label}_{status}_neutral_cards", true, _widget.PeriodCards.All(card => BrushMatches("SeverityNeutralBrush", card.Accent)));
                string healthKey = status switch { RefreshStatus.Stale => "SeverityYellowBrush", RefreshStatus.Error => "SeverityRedBrush", _ => "SeverityNeutralBrush" };
                Check($"{label}_{status}_connection_health_color", true, ConnectionDotMatches(details, healthKey));
                Check($"{label}_{status}_floating_inline_health_stays_visible", true, FloatingHealthMatches(ball, healthKey));
                if (status != RefreshStatus.Stale)
                {
                    Check($"{label}_{status}_no_invented_period", true, !_widget.BallPeriodVisible &&
                        ball.FindName("BallPeriodLabel") is FrameworkElement periodLabel && periodLabel.Visibility == Visibility.Collapsed);
                }
                Check($"{label}_{status}_ball_text_stays_neutral", true, BrushMatches("SeverityNeutralBrush", _widget.BallTextBrush));
                RenderScreenshot(ball, Path.Combine(outputDirectory, $"ball-{label}-{status.ToString().ToLowerInvariant()}-neutral.png"), background);
                RenderTray(_widget.IconState, Path.Combine(outputDirectory, $"tray-{label}-{status.ToString().ToLowerInvariant()}-32.png"), 32);
                RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-{status.ToString().ToLowerInvariant()}-neutral.png"), background);
                ball.Close();
            }
            details.Hide();
            RenderProviderStyles(outputDirectory, label, background);
            RenderFloatingReference(outputDirectory, label, background);
        }
    }

    private static bool BrushMatches(string key, Brush brush) =>
        brush is SolidColorBrush actual && Application.Current.TryFindResource(key) is SolidColorBrush expected && actual.Color == expected.Color;

    /// <summary>
    /// Theme resource rendered for a remaining-quota value, matching
    /// <c>WidgetViewModel.RemainingValueBrush</c>: the yellow tier keeps its darker text
    /// color, every other fresh tier uses its own severity brush.
    /// </summary>
    private static string RemainingValueThemeKey(QuotaSeverity severity) => severity switch
    {
        QuotaSeverity.Green => "SeverityGreenBrush",
        QuotaSeverity.Blue => "SeverityBlueBrush",
        QuotaSeverity.Yellow => "QuotaAmberTextBrush",
        QuotaSeverity.Red => "SeverityRedBrush",
        _ => "SeverityNeutralBrush",
    };

    private static bool ConnectionDotMatches(DetailsWindow details, string key) =>
        FindVisuals<System.Windows.Shapes.Ellipse>(details).Any(dot =>
            System.Windows.Data.BindingOperations.GetBindingExpression(dot, System.Windows.Shapes.Shape.FillProperty)?.ParentBinding.Path?.Path == nameof(WidgetViewModel.ConnectionDotBrush) &&
            BrushMatches(key, dot.Fill));

    private static bool TrayIconMatches(ProviderIconState state)
    {
        using System.Drawing.Icon icon = AppIcon.Create(state);
        using System.Drawing.Bitmap bitmap = icon.ToBitmap();
        string dotKey = state.Status switch
        {
            RefreshStatus.Fresh => "SeverityGreenBrush",
            RefreshStatus.Stale => "SeverityYellowBrush",
            RefreshStatus.Error => "SeverityRedBrush",
            _ => "SeverityNeutralBrush",
        };
        System.Drawing.Color top = bitmap.GetPixel(16, 2);
        bool topMatches = state.Status == RefreshStatus.Stale && state.RemainingPercent is > 0
            ? DrawingColorMatches("SeverityNeutralBrush", top)
            : RenderedIconColorMatches(state.Status == RefreshStatus.Fresh && state.RemainingPercent is > 0, Color.FromArgb(top.A, top.R, top.G, top.B));
        System.Drawing.Color dot = bitmap.GetPixel(27, 27);
        return topMatches && HealthShadeMatches(dotKey, Color.FromArgb(dot.A, dot.R, dot.G, dot.B));
    }

    private static bool DrawingColorMatches(string key, System.Drawing.Color actual) =>
        RenderedColorMatches(key, Color.FromArgb(actual.A, actual.R, actual.G, actual.B));

    private void VerifyQuotaArcPixels(string name, FloatingBallWindow ball, int remaining)
    {
        // Hand-checked sample locations are 1/6, 1/2, 3/4 and 11/12 around the ring.
        // These catch a fixed half-circle, reversed/used-percentage arc, and broken 0/100 ends.
        // A null entry marks a sample this fixture cannot decide: at 14 the arc end sits
        // within half a stroke width of the 1/6 point in one of the two renderings (the tray
        // scales differently from the widget ring), and at 50 the 1/2 point is the arc end
        // itself, so that pixel is a rounded-cap blend rather than arc or track.
        double[] angles = [60, 180, 270, 330];
        bool?[] expected = remaining switch
        {
            0 => [false, false, false, false],
            14 => [null, false, false, false],
            30 or 33 or 34 or 46 => [true, false, false, false],
            50 => [true, null, false, false],
            66 or 67 or 70 => [true, true, false, false],
            78 => [true, true, true, false],
            100 => [true, true, true, true],
            _ => throw new InvalidOperationException("Add hand-checked ring samples for the fixture."),
        };
        string arcKey = RemainingValueThemeKey(QuotaProjection.GetFreshSeverity(remaining));
        using System.Drawing.Icon icon = AppIcon.Create(_widget.IconState);
        using System.Drawing.Bitmap bitmap = icon.ToBitmap();
        for (int i = 0; i < angles.Length; i++)
        {
            if (expected[i] is not { } arcPresent)
            {
                continue;
            }

            string floatingKey = arcPresent ? arcKey : "BallRingTrackBrush";
            Check($"{name}_widget_arc_at_{angles[i]}", true, RenderedColorMatches(floatingKey, SampleRingColor(ball, angles[i])));
            double radians = (angles[i] - 90) * Math.PI / 180;
            int x = (int)Math.Round(16 + 13.75 * Math.Cos(radians));
            int y = (int)Math.Round(16 + 13.75 * Math.Sin(radians));
            System.Drawing.Color pixel = bitmap.GetPixel(x, y);
            Check($"{name}_tray_arc_at_{angles[i]}", true, RenderedIconColorMatches(arcPresent, Color.FromArgb(pixel.A, pixel.R, pixel.G, pixel.B)));
        }
    }

    private void RenderTray(ProviderIconState state, string path, int size)
    {
        using System.Drawing.Bitmap bitmap = AppIcon.RenderBitmap(state, size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        _screenshots.Add(Path.GetFileName(path));
    }

    private void RenderProviderStyles(string outputDirectory, string label, Brush background)
    {
        var sheet = new System.Windows.Controls.StackPanel { Width = 640, Background = background };
        sheet.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = (label == "light" ? "浅色" : "深色") + " · 托盘样式预留 · 仅 Codex 已接入 / TRAY PREVIEW",
            Foreground = ProviderIconState.ResourceBrush("TextPrimaryBrush"),
            FontSize = 16,
            Margin = new Thickness(16, 12, 16, 16),
        });
        var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        foreach (ProviderIconAppearance appearance in ProviderIconAppearance.PreviewStyles)
        {
            var state = new ProviderIconState(appearance, 46, RefreshStatus.Fresh, ProviderIconState.CurrentThemeIsDark);
            string largePath = Path.Combine(outputDirectory, $"tray-{label}-{appearance.Name.ToLowerInvariant()}-128.png");
            RenderTray(state, largePath, 128);
            var column = new System.Windows.Controls.StackPanel { Width = 160 };
            column.Children.Add(new System.Windows.Controls.Image { Source = LoadPreviewImage(largePath), Width = 128, Height = 128, Margin = new Thickness(0, 0, 0, 10) });
            column.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = appearance.Name + (appearance == ProviderIconAppearance.Codex ? " · 已接入" : " · 样式预留"),
                Foreground = ProviderIconState.ResourceBrush("TextSecondaryBrush"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16),
            });
            row.Children.Add(column);
            RenderTray(state, Path.Combine(outputDirectory, $"tray-{label}-{appearance.Name.ToLowerInvariant()}-16.png"), 16);
            RenderTray(state, Path.Combine(outputDirectory, $"tray-{label}-{appearance.Name.ToLowerInvariant()}-32.png"), 32);
            var trayRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
            };
            foreach (int size in new[] { 16, 32 })
            {
                trayRow.Children.Add(new System.Windows.Controls.Image
                {
                    Source = LoadPreviewImage(Path.Combine(outputDirectory, $"tray-{label}-{appearance.Name.ToLowerInvariant()}-{size}.png")),
                    Width = size,
                    Height = size,
                    Margin = new Thickness(6, 0, 6, 0),
                });
            }
            column.Children.Add(trayRow);
            column.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "托盘 16 / 32 px",
                Foreground = ProviderIconState.ResourceBrush("TextSecondaryBrush"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }
        sheet.Children.Add(row);
        sheet.Measure(new Size(640, double.PositiveInfinity));
        sheet.Arrange(new Rect(new Point(), sheet.DesiredSize));
        sheet.UpdateLayout();
        RenderElement(sheet, Path.Combine(outputDirectory, $"tray-styles-{label}.png"), background);
    }

    private static BitmapImage LoadPreviewImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void RenderFloatingReference(string outputDirectory, string label, Brush background)
    {
        var model = new WidgetViewModel(BuildState(76, RefreshStatus.Fresh));
        var ball = new FloatingBallWindow { DataContext = model };
        Layout(ball, BallSize, BallSize);
        var closeup = new System.Windows.Controls.Grid { Width = 216, Height = 216, Background = background };
        closeup.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new VisualBrush((Visual)ball.Content), IsHitTestVisible = false });
        closeup.Measure(new Size(216, 216));
        closeup.Arrange(new Rect(0, 0, 216, 216));
        closeup.UpdateLayout();
        RenderElement(closeup, Path.Combine(outputDirectory, $"floating-reference-{label}.png"), background);
        ball.Close();
    }

    private void RenderCombinedProviderStyles(string outputDirectory)
    {
        var sheet = new System.Windows.Controls.StackPanel { Width = 640 };
        foreach (string theme in new[] { "light", "dark" })
        {
            sheet.Children.Add(new System.Windows.Controls.Image { Source = LoadPreviewImage(Path.Combine(outputDirectory, $"tray-styles-{theme}.png")) });
        }
        sheet.Measure(new Size(640, double.PositiveInfinity));
        sheet.Arrange(new Rect(new Point(), sheet.DesiredSize));
        sheet.UpdateLayout();
        RenderElement(sheet, Path.Combine(outputDirectory, "tray-styles-light-dark.png"), Brushes.Black);
        var floating = new System.Windows.Controls.StackPanel { Width = 432, Orientation = System.Windows.Controls.Orientation.Horizontal };
        foreach (string theme in new[] { "light", "dark" })
        {
            floating.Children.Add(new System.Windows.Controls.Image { Source = LoadPreviewImage(Path.Combine(outputDirectory, $"floating-reference-{theme}.png")), Width = 216 });
        }
        floating.Measure(new Size(432, double.PositiveInfinity));
        floating.Arrange(new Rect(new Point(), floating.DesiredSize));
        floating.UpdateLayout();
        RenderElement(floating, Path.Combine(outputDirectory, "floating-reference-light-dark.png"), Brushes.Black);
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is Window window && window.Content is DependencyObject content) { root = content; }
        if (root is T match) { yield return match; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (T child in FindVisuals<T>(VisualTreeHelper.GetChild(root, i))) { yield return child; }
        }
    }

    private static bool VisibleTextFits(DetailsWindow details) =>
        FindVisuals<System.Windows.Controls.TextBlock>(details)
            .Where(block => block.IsVisible && (block.Name is "DailySessionValue" or "DailyEstimateValue" || block.Text.StartsWith("重置 ", StringComparison.Ordinal)))
            .All(block => block.ActualWidth + block.Margin.Left + block.Margin.Right + 1 >= block.DesiredSize.Width &&
                block.ActualHeight + block.Margin.Top + block.Margin.Bottom + 1 >= block.DesiredSize.Height);

    private static bool HasSeverityAdjective(string? text) =>
        text is not null && (text.Contains("充足", StringComparison.Ordinal) ||
                             text.Contains("偏低", StringComparison.Ordinal) ||
                             text.Contains("紧张", StringComparison.Ordinal));

    private static bool HeaderMatchesReference(DependencyObject root)
    {
        var blocks = new List<System.Windows.Controls.TextBlock>();
        void Visit(DependencyObject node)
        {
            if (node is Window window && window.Content is DependencyObject content) { Visit(content); return; }
            if (node is System.Windows.Controls.TextBlock block) { blocks.Add(block); }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { Visit(VisualTreeHelper.GetChild(node, i)); }
        }
        Visit(root);
        var title = blocks.FirstOrDefault(block => block.Text == "主额度（5 小时）");
        var remainder = blocks.FirstOrDefault(block => block.Text == "46%");
        if (title is null || remainder is null || root is not Visual visual) { return false; }
        Point a = title.TransformToAncestor(visual).Transform(new Point());
        Point b = remainder.TransformToAncestor(visual).Transform(new Point());
        return b.X > a.X + title.ActualWidth && Math.Abs((a.Y + title.ActualHeight / 2) - (b.Y + remainder.ActualHeight / 2)) <= 2;
    }

    private void Check(string name, object? expected, object? actual)
    {
        bool passed = Equals(expected, actual);
        _checks.Add(new Dictionary<string, object?>
        {
            ["check"] = name,
            ["expected"] = expected?.ToString(),
            ["actual"] = actual?.ToString(),
            ["passed"] = passed,
        });

        if (!passed)
        {
            Console.WriteLine($"FAIL {name}: expected {expected}, actual {actual}");
        }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is Window window && window.Content is DependencyObject content) { return FindVisual<T>(content); }
        if (root is T match) { return match; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindVisual<T>(VisualTreeHelper.GetChild(root, i)) is { } child) { return child; }
        }
        return null;
    }

    private static bool ContainsVisualText(DependencyObject root, string text)
    {
        if (root is Window window && window.Content is DependencyObject content) { return ContainsVisualText(content, text); }
        if (root is System.Windows.Controls.TextBlock block && block.Text == text) { return true; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (ContainsVisualText(VisualTreeHelper.GetChild(root, i), text)) { return true; }
        }
        return false;
    }

    /// <summary>Concatenates every rendered text fragment so assertion failures are explicit.</summary>
    private static string CollectVisualText(DependencyObject root)
    {
        var builder = new StringBuilder();
        Collect(root, builder);
        return builder.ToString();

        static void Collect(DependencyObject node, StringBuilder into)
        {
            if (node is Window window && window.Content is DependencyObject content) { Collect(content, into); return; }
            if (node is System.Windows.Controls.TextBlock block && !string.IsNullOrEmpty(block.Text)) { into.Append(block.Text).Append('\n'); }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Collect(VisualTreeHelper.GetChild(node, i), into);
            }
        }
    }

    /// <summary>
    /// Lets WPF process DynamicResource invalidation so an existing window re-resolves
    /// theme brushes, proving a theme switch updates open windows.
    /// </summary>
    private static async Task FlushResourceChangesAsync(params FrameworkElement[] elements)
    {
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        foreach (FrameworkElement element in elements)
        {
            if (element is DetailsWindow details) { MonitorService.SizePanelToWorkArea(details, double.PositiveInfinity); }
            element.InvalidateVisual();
            element.UpdateLayout();
        }

        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
    }

    private void CheckPanelBackground(string name, Window window, byte expectedRed)
    {
        if (window.Content is System.Windows.Controls.Border border && border.Background is SolidColorBrush brush)
        {
            Check(name, expectedRed, brush.Color.R);
        }
        else
        {
            Check(name, expectedRed, "no-brush");
        }
    }

    /// <summary>
    /// Renders the ball's content and samples the pixel at the top of the ring's arc, so
    /// the actually drawn theme color is asserted rather than only the bound brush.
    /// </summary>
    private static System.Windows.Media.Color? SampleRingTopColor(FloatingBallWindow ball) => SampleRingColor(ball, 0);

    private static bool WidgetCornersAreTransparent(FloatingBallWindow ball)
    {
        var root = (FrameworkElement)ball.Content;
        var bitmap = new RenderTargetBitmap(72, 72, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        foreach (Int32Rect corner in new[] { new Int32Rect(8, 8, 1, 1), new Int32Rect(64, 8, 1, 1), new Int32Rect(8, 64, 1, 1), new Int32Rect(64, 64, 1, 1) })
        {
            var pixel = new byte[4];
            bitmap.CopyPixels(corner, pixel, 4, 0);
            if (pixel[3] != 0) { return false; }
        }
        return true;
    }

    private static Color SampleWidgetPixel(FloatingBallWindow ball, int x, int y)
    {
        var bitmap = new RenderTargetBitmap(72, 72, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)ball.Content);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>
    /// Samples the actually rendered docked-strip pixel at a relative position inside
    /// <paramref name="target"/> (render transform included), so track versus remaining fill
    /// is compared where the user sees it rather than from binding values alone.
    /// </summary>
    private static Color? SampleStripPixel(FloatingBallWindow ball, FrameworkElement target, double relativeX, double relativeY)
    {
        if (ball.Content is not FrameworkElement root || target.ActualWidth < 1 || target.ActualHeight < 1)
        {
            return null;
        }

        const int resolution = 8;
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * resolution));
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * resolution));
        var bitmap = new RenderTargetBitmap(width, height, 96 * resolution, 96 * resolution, PixelFormats.Pbgra32);
        bitmap.Render(root);
        Point point = target.TransformToAncestor(root).Transform(new Point(target.ActualWidth * relativeX, target.ActualHeight * relativeY));
        int x = Math.Clamp((int)Math.Round(point.X * resolution), 0, width - 1);
        int y = Math.Clamp((int)Math.Round(point.Y * resolution), 0, height - 1);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static bool IsWhiteBadgePixel(Color color) => color.A >= 240 && color.R >= 230 && color.G >= 230 && color.B >= 230;

    private static bool FloatingHealthMatches(FloatingBallWindow ball, string key)
    {
        if (ball.FindName("StatusDot") is not System.Windows.Shapes.Ellipse { Fill: SolidColorBrush } dot || dot.Visibility != Visibility.Visible || dot.ActualWidth != 6 || dot.ActualHeight != 6)
        {
            return false;
        }
        Point center = dot.TransformToAncestor((Visual)ball.Content).Transform(new Point(3, 3));
        return RenderedColorMatches(key, SampleWidgetPixel(ball, (int)Math.Round(center.X), (int)Math.Round(center.Y)));
    }

    private static bool RenderedIconColorMatches(bool goldArc, Color? actual)
    {
        if (actual is not { A: >= 200 } color) { return false; }
        // Tone ranges are hand-checked independently: gold remains distinct from the
        // near-neutral track despite deliberate shading. Geometry expectations stay exact.
        if (goldArc) { return color.R >= 220 && color.G >= 140 && color.B <= 150; }
        if (Application.Current.TryFindResource("BallRingTrackBrush") is not SolidColorBrush track) { return false; }
        return Math.Abs(color.R - track.Color.R) <= 24 && Math.Abs(color.G - track.Color.G) <= 24 && Math.Abs(color.B - track.Color.B) <= 24;
    }

    private static bool HealthShadeMatches(string key, Color actual) =>
        Application.Current.TryFindResource(key) is SolidColorBrush expected && actual.A >= 240 &&
        Math.Abs(actual.R - expected.Color.R) <= 20 && Math.Abs(actual.G - expected.Color.G) <= 20 && Math.Abs(actual.B - expected.Color.B) <= 20;

    private void VerifyStableArcGradient()
    {
        using System.Drawing.Bitmap partial = AppIcon.RenderBitmap(new ProviderIconState(ProviderIconAppearance.Gemini, 24, RefreshStatus.Fresh, ProviderIconState.CurrentThemeIsDark), 128);
        using System.Drawing.Bitmap full = AppIcon.RenderBitmap(new ProviderIconState(ProviderIconAppearance.Gemini, 100, RefreshStatus.Fresh, ProviderIconState.CurrentThemeIsDark), 128);
        System.Drawing.Color a = partial.GetPixel(112, 37);
        System.Drawing.Color b = full.GetPixel(112, 37);
        Check("tray_gradient_stays_at_same_circle_location_when_quota_changes", true,
            a.A >= 240 && b.A >= 240 && Math.Abs(a.R - b.R) <= 4 && Math.Abs(a.G - b.G) <= 4 && Math.Abs(a.B - b.B) <= 4);
    }

    private static System.Windows.Media.Color? SampleRingColor(FloatingBallWindow ball, double clockwiseAngle)
    {
        ProgressRing? ring = FindVisual<ProgressRing>(ball);
        if (ring is null || ball.Content is not FrameworkElement root || ring.ActualWidth < 4 || ring.ActualHeight < 4)
        {
            return null;
        }

        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        double radius = (Math.Min(ring.ActualWidth, ring.ActualHeight) - Math.Max(1d, ring.RingThickness)) / 2d;
        double radians = (clockwiseAngle - 90) * Math.PI / 180;
        Point top = ring.TransformToAncestor(root).Transform(new Point(ring.ActualWidth / 2d + radius * Math.Cos(radians), ring.ActualHeight / 2d + radius * Math.Sin(radians)));
        int x = Math.Clamp((int)Math.Round(top.X), 0, width - 1);
        int y = Math.Clamp((int)Math.Round(top.Y), 0, height - 1);

        var pixels = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return System.Windows.Media.Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }

    private static bool RenderedColorMatches(string themeKey, System.Windows.Media.Color? actual)
    {
        if (Application.Current?.TryFindResource(themeKey) is not SolidColorBrush expected || actual is not { } color)
        {
            return false;
        }

        return color.A >= 200
            && Math.Abs(color.R - expected.Color.R) <= 8
            && Math.Abs(color.G - expected.Color.G) <= 8
            && Math.Abs(color.B - expected.Color.B) <= 8;
    }

    private async Task VerifySettingsControlsAsync(SettingsWindow settingsWindow, DetailsWindow details, string outputDirectory)
    {
        details.Show();
        await FlushResourceChangesAsync(settingsWindow);
        var remaining = (System.Windows.Controls.CheckBox)settingsWindow.FindName("ShowRemainingBox");
        var reset = (System.Windows.Controls.CheckBox)settingsWindow.FindName("ShowResetBox");
        var today = (System.Windows.Controls.CheckBox)settingsWindow.FindName("ShowTodayBox");
        string ballText = _widget.BallText;
        for (int mask = 0; mask < 8; mask++)
        {
            remaining.IsChecked = (mask & 1) != 0;
            reset.IsChecked = (mask & 2) != 0;
            today.IsChecked = (mask & 4) != 0;
            _widget.UpdateSettings(_settings.ToSettings());
            await FlushResourceChangesAsync(details, settingsWindow);
            var bars = FindVisuals<System.Windows.Controls.ProgressBar>(details).ToArray();
            var resets = FindVisuals<System.Windows.Controls.TextBlock>(details).Where(text =>
                System.Windows.Data.BindingOperations.GetBinding(text, System.Windows.Controls.TextBlock.TextProperty)?.Path.Path == "ResetText").ToArray();
            Check($"display_combination_{mask}_applies_only_to_details", true,
                _settings.ShowRemaining == ((mask & 1) != 0) && _settings.ShowResetTime == ((mask & 2) != 0) &&
                _settings.ShowTodayUsage == ((mask & 4) != 0) &&
                bars.Length == 2 && bars.All(bar => bar.IsVisible == ((mask & 1) != 0)) &&
                resets.Length == 2 && resets.All(text => text.IsVisible == ((mask & 2) != 0)) &&
                ((FrameworkElement)details.FindName("DailyCard")).IsVisible == ((mask & 4) != 0) && _widget.BallText == ballText);
        }
        await FlushResourceChangesAsync(settingsWindow);
        var opacity = (System.Windows.Controls.Slider)settingsWindow.FindName("OpacitySlider");
        var size = (System.Windows.Controls.Slider)settingsWindow.FindName("SizeSlider");
        await VerifySliderInputAsync(settingsWindow, opacity, 70);
        await VerifySliderInputAsync(settingsWindow, size, 110);
        opacity.Value = 40;
        size.Value = 150;
        Check("sliders_update_draft_at_bounds", true, _settings.OpacityPercent == 40 && _settings.BallSizePercent == 150 && size.Minimum == 75 && size.Maximum == 150);
        var interval = (System.Windows.Controls.ComboBox)settingsWindow.FindName("RefreshCombo");
        _settings.IntervalSeconds = 45;
        await FlushResourceChangesAsync(settingsWindow);
        Check("custom_interval_survives_dropdown_binding", true, interval.SelectedItem is IntervalOption { Seconds: 45 } && interval.Items.Cast<IntervalOption>().Any(option => option.Seconds == 45));
        interval.SelectedItem = interval.Items.Cast<IntervalOption>().First(option => option.Seconds == 300);
        Check("refresh_dropdown_updates_draft", 300, _settings.IntervalSeconds);
        var threshold = (System.Windows.Controls.TextBox)settingsWindow.FindName("ThresholdInput");
        var save = (System.Windows.Controls.Button)settingsWindow.FindName("SaveButton");
        var notifications = (System.Windows.Controls.CheckBox)settingsWindow.FindName("NotificationsToggle");
        Check("disabled_notifications_keep_threshold_inactive_and_retained", true, !threshold.IsEnabled && _settings.LowQuotaThreshold == 20);
        notifications.IsChecked = true;
        await FlushResourceChangesAsync(settingsWindow);
        foreach (string invalid in new[] { "", "abc", "0", "101", "20.5" })
        {
            threshold.Text = invalid;
            await FlushResourceChangesAsync(settingsWindow);
            Check($"invalid_threshold_{invalid}_blocks_save", true, System.Windows.Controls.Validation.GetHasError(threshold) && !save.IsEnabled);
        }
        threshold.Text = "25";
        await FlushResourceChangesAsync(settingsWindow);
        Check("valid_threshold_updates_draft_and_enables_save", true, _settings.LowQuotaThreshold == 25 && save.IsEnabled);
        threshold.Text = "invalid";
        notifications.IsChecked = false;
        await FlushResourceChangesAsync(settingsWindow);
        int saves = 0;
        void Saved() => saves++;
        settingsWindow.SaveRequested += Saved;
        save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("disabled_threshold_error_does_not_block_other_preferences", true, !threshold.IsEnabled && save.IsEnabled && saves == 1 && _settings.LowQuotaThreshold == 25);
        notifications.IsChecked = true;
        await FlushResourceChangesAsync(settingsWindow);
        save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("reenabled_invalid_threshold_guards_save", true, threshold.IsEnabled && !save.IsEnabled && saves == 1);
        settingsWindow.SaveRequested -= Saved;
        threshold.Text = "25";
        await FlushResourceChangesAsync(settingsWindow);
        var up = (System.Windows.Controls.Button)settingsWindow.FindName("ThresholdUp");
        var down = (System.Windows.Controls.Button)settingsWindow.FindName("ThresholdDown");
        up.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("vector_step_up_changes_threshold", 26, _settings.LowQuotaThreshold);
        down.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check("vector_step_down_changes_threshold", 25, _settings.LowQuotaThreshold);
        Check("step_icons_are_vector_paths", true, up.Content is System.Windows.Shapes.Path && down.Content is System.Windows.Shapes.Path);
        interval.ApplyTemplate();
        var intervalSurface = (System.Windows.Controls.Border)interval.Template.FindName("SelectionSurface", interval);
        Check("dropdown_arrow_is_round_vector_path", true, FindVisual<System.Windows.Shapes.Path>(intervalSurface) is { StrokeThickness: 1.6 });
        Check("theme_stays_available_in_common_settings", true, ((FrameworkElement)settingsWindow.FindName("ThemeCombo")).IsVisible);
        ((System.Windows.Controls.Button)settingsWindow.FindName("RestoreDefaultsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        _widget.UpdateSettings(_settings.ToSettings());
        await FlushResourceChangesAsync(details, settingsWindow);
        Check("restore_defaults_updates_controls_and_keeps_ball_value", true, _settings.BallSizePercent == 100 && _settings.OpacityPercent == 95 && _settings.IntervalSeconds == 120 && _settings.SelectedPlacement.Placement == BallPlacement.Custom && _settings.ShowRemaining && _settings.ShowResetTime && _settings.ShowTodayUsage && !_settings.NotificationsEnabled && _settings.LowQuotaThreshold == 20 && _widget.BallText == ballText);
        // Keep the main reference render free of the action status line.
        _settings.LoadFrom(WidgetSettings.Default, _executablePath, "已找到官方 Codex 客户端（验证）");
        _widget.UpdateSettings(WidgetSettings.Default);
        await FlushResourceChangesAsync(details, settingsWindow);
        var body = FindVisual<System.Windows.Controls.ScrollViewer>(settingsWindow);
        Check("reference_controls_fit_without_body_scroll", true, body is not null && body.ScrollableHeight <= 1);
        double preferredHeight = settingsWindow.Height;
        foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            _themeService.Apply(theme);
            string label = theme.ToString().ToLowerInvariant();
            Brush background = theme == ThemeMode.Light ? Brushes.WhiteSmoke : Brushes.Black;
            foreach (var values in new[] { (Name: "min", Opacity: 40, Size: 75), (Name: "mid", Opacity: 70, Size: 110), (Name: "max", Opacity: 100, Size: 150) })
            {
                opacity.Value = values.Opacity;
                size.Value = values.Size;
                await FlushResourceChangesAsync(settingsWindow);
                RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, $"settings-{label}-sliders-{values.Name}.png"), background);
            }
            opacity.Value = 70;
            opacity.Focus();
            opacity.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(opacity), Environment.TickCount, System.Windows.Input.Key.Right)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            await FlushResourceChangesAsync(settingsWindow);
            CheckSliderFocus(opacity, label + "_adjusted_opacity");
            CheckSliderDraft(settingsWindow, opacity, label + "_focused_keyboard_adjustment");
            RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, $"settings-{label}-opacity-focused.png"), background);
            size.Focus();
            await FlushResourceChangesAsync(settingsWindow);
            CheckSliderFocus(size, label + "_size");
            RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, $"settings-{label}-size-focused.png"), background);
            notifications.Focus();
            foreach (bool enabled in new[] { true, false })
            {
                notifications.IsChecked = enabled;
                await FlushResourceChangesAsync(settingsWindow);
                CheckThresholdAlignment(settingsWindow, threshold, $"{label}_threshold_{(enabled ? "enabled" : "disabled")}");
                RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, $"settings-{label}-threshold-{(enabled ? "enabled" : "disabled")}.png"), background);
            }
            MonitorService.SizePanelToWorkArea(settingsWindow, 374);
            await FlushResourceChangesAsync(settingsWindow);
            body!.ScrollToTop();
            await FlushResourceChangesAsync(settingsWindow);
            CheckSettingsScrollSurface(settingsWindow, body, label + "_short_settings");
            RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, $"settings-{label}-short-height-top.png"), background);
            body.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
            await FlushResourceChangesAsync(settingsWindow);
            Check($"{label}_hidden_settings_scroll_wheel_access", true, body.VerticalOffset > 0);
            double wheelOffset = body.VerticalOffset;
            body.Focus();
            body.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(body), Environment.TickCount, System.Windows.Input.Key.PageDown)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            await FlushResourceChangesAsync(settingsWindow);
            Check($"{label}_hidden_settings_scroll_keyboard_access", true, body.VerticalOffset > wheelOffset);
            interval.Focus();
            interval.BringIntoView();
            await FlushResourceChangesAsync(settingsWindow);
            Rect intervalBounds = interval.TransformToAncestor(body).TransformBounds(new Rect(0, 0, interval.ActualWidth, interval.ActualHeight));
            Check($"{label}_overflow_control_focus_is_reachable", true, interval.IsKeyboardFocusWithin && intervalBounds.Top >= -1 && intervalBounds.Bottom <= body.ActualHeight + 1);
            body.ScrollToEnd();
            await FlushResourceChangesAsync(settingsWindow);
            var defaults = (FrameworkElement)settingsWindow.FindName("RestoreDefaultsButton");
            Check($"{label}_short_height_keeps_vertical_scroll_and_fixed_footer", true, settingsWindow.Height == 350 && body.ScrollableHeight > 0 && body.ScrollableWidth <= 1 && save.IsVisible && defaults.IsVisible && new[] { (FrameworkElement)save, defaults }.All(button => button.TransformToAncestor((Visual)settingsWindow.Content).Transform(new Point()).Y + button.ActualHeight <= 350));
            RenderScreenshot(settingsWindow, Path.Combine(outputDirectory, $"settings-{label}-short-height.png"), background);
            settingsWindow.Height = preferredHeight;
            body.ScrollToTop();
            await FlushResourceChangesAsync(settingsWindow);
            Check($"{label}_normal_settings_common_controls_fit", true, body.ScrollableHeight <= 1 && body.ScrollableWidth <= 1);
            CheckSettingsScrollSurface(settingsWindow, body, label + "_normal_settings");
        }
        _themeService.Apply(ThemeMode.Light);
        _settings.LoadFrom(WidgetSettings.Default, _executablePath, "已找到官方 Codex 客户端（验证）");
        await FlushResourceChangesAsync(settingsWindow);
        settingsWindow.Height = 350;
        await FlushResourceChangesAsync(settingsWindow);
        Check("small_work_area_keeps_scroll_and_fixed_save_footer", true, body!.ScrollableHeight > 0 && save.TransformToAncestor((Visual)settingsWindow.Content).Transform(new Point()).Y + save.ActualHeight <= 350);
        settingsWindow.Height = preferredHeight;
        await FlushResourceChangesAsync(settingsWindow);
    }

    private void CheckSettingsScrollSurface(SettingsWindow window, System.Windows.Controls.ScrollViewer body, string name)
    {
        Check(name + "_has_no_visible_scrollbar_or_channel", true,
            !FindVisuals<System.Windows.Controls.Primitives.ScrollBar>(body).Any(bar => bar.IsVisible) &&
            Math.Abs(body.ViewportWidth - body.ActualWidth) <= 1 && body.ScrollableWidth <= 1);
        _checks[^1]["viewportWidth"] = body.ViewportWidth;
        _checks[^1]["bodyWidth"] = body.ActualWidth;
    }

    private static void Layout(Window window, double width, double height)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        if (window is DetailsWindow details && !double.IsFinite(height)) { MonitorService.SizePanelToWorkArea(details, double.PositiveInfinity); }
        if (!double.IsFinite(height) && window.SizeToContent == SizeToContent.Manual) { height = window.Height; }
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, double.IsFinite(height) ? height : root.DesiredSize.Height));
        root.UpdateLayout();
    }

    private async Task VerifySliderInputAsync(SettingsWindow window, System.Windows.Controls.Slider slider, double midpoint)
    {
        slider.ApplyTemplate();
        var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
        slider.Focus();
        await FlushResourceChangesAsync(window);
        CheckSliderFocus(slider, slider.Name);
        foreach (double value in new[] { slider.Minimum, midpoint, slider.Maximum })
        {
            slider.Value = value;
            await FlushResourceChangesAsync(window);
            Rect thumb = track.Thumb.TransformToAncestor(slider).TransformBounds(new Rect(0, 0, track.Thumb.ActualWidth, track.Thumb.ActualHeight));
            double x = thumb.Left > slider.ActualWidth / 2 ? slider.ActualWidth / 4 : slider.ActualWidth * 3 / 4;
            bool nativeHit = new[] { 2d, slider.ActualHeight / 2, slider.ActualHeight - 2 }.All(y =>
            {
                var hit = slider.InputHitTest(new Point(x, y)) as DependencyObject;
                while (hit is not null && hit is not System.Windows.Controls.Primitives.RepeatButton) { hit = VisualTreeHelper.GetParent(hit); }
                return hit is System.Windows.Controls.Primitives.RepeatButton;
            });
            Check($"{slider.Name}_{value}_near_rail_has_native_track_button_hit", true, nativeHit);
            Check($"{slider.Name}_{value}_thumb_bounds_are_unclipped", true, thumb.Left >= -0.1 && thumb.Right <= slider.ActualWidth + 0.1 && thumb.Top >= -0.1 && thumb.Bottom <= slider.ActualHeight + 0.1);
            CheckSliderDraft(window, slider, $"{value}_bound_value");
        }

        slider.Value = slider.Minimum;
        await FlushResourceChangesAsync(window);
        double available = track.ActualWidth - track.Thumb.ActualWidth;
        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(available * 10 / (slider.Maximum - slider.Minimum), 0)
        { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
        Check($"{slider.Name}_native_thumb_drag_updates_value", slider.Minimum + 10, slider.Value);
        CheckSliderDraft(window, slider, "native_drag_bound_value");
        slider.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), Environment.TickCount, System.Windows.Input.Key.Right)
        { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        Check($"{slider.Name}_keyboard_right_uses_small_step", slider.Minimum + 10 + slider.SmallChange, slider.Value);
        slider.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), Environment.TickCount, System.Windows.Input.Key.Left)
        { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        Check($"{slider.Name}_keyboard_left_uses_small_step", slider.Minimum + 10, slider.Value);
        slider.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), Environment.TickCount, System.Windows.Input.Key.End)
        { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        Check($"{slider.Name}_keyboard_end_reaches_maximum", slider.Maximum, slider.Value);
        CheckSliderDraft(window, slider, "keyboard_end_bound_value");
        slider.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(slider), Environment.TickCount, System.Windows.Input.Key.Home)
        { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
        Check($"{slider.Name}_keyboard_home_reaches_minimum", slider.Minimum, slider.Value);
        CheckSliderDraft(window, slider, "keyboard_home_bound_value");
        System.Windows.Controls.Slider.IncreaseLarge.Execute(null, slider);
        Check($"{slider.Name}_native_track_command_uses_large_step", slider.Minimum + slider.LargeChange, slider.Value);
        await FlushResourceChangesAsync(window);
        // Native Track geometry is used by WPF's move-to-point handler. This checks its
        // coordinate projection and enabled policy, not physical pointer injection.
        double pointValue = track.ValueFromPoint(new Point(track.Thumb.ActualWidth / 2 + available * 0.75, track.ActualHeight / 2));
        Check($"{slider.Name}_move_to_point_policy_and_native_projection", true, slider.IsMoveToPointEnabled && Math.Abs(pointValue - (slider.Minimum + (slider.Maximum - slider.Minimum) * 0.75)) < 0.01);
        _checks[^1]["projectedValue"] = pointValue;
        foreach (double fraction in new[] { 0.2, 0.73 })
        {
            var point = new Point(track.Thumb.ActualWidth / 2 + available * fraction, 2);
            Point sliderPoint = track.TransformToAncestor(slider).Transform(point);
            bool handled = RaiseSyntheticTrackClick(slider, sliderPoint);
            double expected = slider.Minimum + Math.Round((slider.Maximum - slider.Minimum) * fraction / slider.TickFrequency) * slider.TickFrequency;
            Check($"{slider.Name}_routed_track_click_{fraction}_moves_to_snapped_position", true, handled && slider.Value == expected);
            await FlushResourceChangesAsync(window);
            CheckSliderDraft(window, slider, $"routed_click_{fraction}_bound_value");
        }
    }

    private void CheckSliderFocus(System.Windows.Controls.Slider slider, string name)
    {
        var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
        track.Thumb.ApplyTemplate();
        var knob = (System.Windows.Shapes.Ellipse)track.Thumb.Template.FindName("Knob", track.Thumb);
        Check($"{name}_keyboard_focus_visible_on_round_knob", true, slider.IsKeyboardFocusWithin && BrushMatches("FocusBrush", knob.Stroke) && knob.StrokeThickness >= 2);
        Check($"{name}_focus_has_no_outer_rectangle", true, !FindVisuals<System.Windows.Controls.Border>(slider).Any(border => border.ActualHeight > 20 && border.BorderBrush is SolidColorBrush brush && brush.Color.A > 0 && border.BorderThickness != new Thickness(0)));
    }

    private void CheckThresholdAlignment(SettingsWindow window, System.Windows.Controls.TextBox threshold, string name)
    {
        var field = (System.Windows.Controls.Grid)threshold.Parent;
        var row = (System.Windows.Controls.StackPanel)field.Parent;
        Rect text = threshold.GetRectFromCharacterIndex(0);
        double textCenter = threshold.TransformToAncestor(row).Transform(new Point(text.X, text.Y + text.Height / 2)).Y;
        double fieldCenter = field.TransformToAncestor(row).Transform(new Point(0, field.ActualHeight / 2)).Y;
        var labels = row.Children.OfType<System.Windows.Controls.TextBlock>().ToArray();
        var up = (FrameworkElement)window.FindName("ThresholdUp");
        var down = (FrameworkElement)window.FindName("ThresholdDown");
        double stepCenter = (up.TransformToAncestor(row).Transform(new Point()).Y + down.TransformToAncestor(row).Transform(new Point(0, down.ActualHeight)).Y) / 2;
        Check(name + "_text_field_labels_and_stepper_are_centered", true, !text.IsEmpty && Math.Abs(textCenter - fieldCenter) <= 2 && Math.Abs(stepCenter - fieldCenter) <= 1 && labels.All(label => Math.Abs(label.TransformToAncestor(row).Transform(new Point(0, label.ActualHeight / 2)).Y - fieldCenter) <= 1));
        _checks[^1]["textCenterOffsetDip"] = Math.Round(textCenter - fieldCenter, 2);
        _checks[^1]["stepperCenterOffsetDip"] = Math.Round(stepCenter - fieldCenter, 2);
        Check(name + "_text_and_vector_arrows_are_unclipped", true, text.Top >= 0 && text.Bottom <= threshold.ActualHeight && new[] { up, down }.All(button =>
        {
            var arrow = (FrameworkElement)((System.Windows.Controls.Button)button).Content;
            Rect bounds = arrow.TransformToAncestor(button).TransformBounds(new Rect(0, 0, arrow.ActualWidth, arrow.ActualHeight));
            return bounds.Left >= 0 && bounds.Right <= button.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= button.ActualHeight;
        }));
    }

    private static bool RaiseSyntheticTrackClick(System.Windows.Controls.Slider slider, Point point)
    {
        // WPF reads its cached device coordinates, not the MouseButtonEventArgs position.
        // Scope this harness-only input snapshot to one synchronous routed event, then
        // restore it. No native cursor operation or physical input is performed.
        var fields = new[] { "_inputSource", "_lastPosition", "_stylusDevice" }.Select(name =>
            typeof(System.Windows.Input.MouseDevice).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new NotSupportedException("This WPF runtime does not expose the expected verification-only mouse snapshot: " + name)).ToArray();
        var device = System.Windows.Input.Mouse.PrimaryDevice;
        object?[] previous = fields.Select(field => field.GetValue(device)).ToArray();
        var source = PresentationSource.FromVisual(slider) as System.Windows.Interop.HwndSource
            ?? throw new NotSupportedException("Native WPF presentation source required for routed slider verification.");
        var target = slider.InputHitTest(point) as UIElement
            ?? throw new InvalidOperationException("Track click must target the real slider hit surface.");
        Point rootPoint = slider.TransformToAncestor(source.RootVisual).Transform(point);
        try
        {
            fields[0].SetValue(device, source);
            fields[1].SetValue(device, source.CompositionTarget.TransformToDevice.Transform(rootPoint));
            fields[2].SetValue(device, null);
            var input = new System.Windows.Input.MouseButtonEventArgs(device, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent };
            target.RaiseEvent(input);
            return input.Handled;
        }
        finally
        {
            for (int i = 0; i < fields.Length; i++) { fields[i].SetValue(device, previous[i]); }
        }
    }

    private void CheckSliderDraft(SettingsWindow window, System.Windows.Controls.Slider slider, string suffix)
    {
        window.UpdateLayout();
        double draft = slider.Name == "OpacitySlider" ? _settings.OpacityPercent : _settings.BallSizePercent;
        string label = ((System.Windows.Controls.TextBlock)window.FindName(slider.Name == "OpacitySlider" ? "OpacityValue" : "SizeValue")).Text;
        string expectedLabel = slider.Name == "OpacitySlider" ? draft.ToString("0", CultureInfo.CurrentCulture) + "%" : _settings.BallSizeCaption;
        string? sizeTooltip = slider.ToolTip as string;
        Check($"{slider.Name}_{suffix}_matches_draft_and_display", true, draft == slider.Value && label == expectedLabel &&
            (slider.Name == "OpacitySlider" || sizeTooltip == draft.ToString("0", CultureInfo.CurrentCulture) + "%"));
    }

    /// <summary>
    /// Renders the collapsed side strip and its inward reveal for both themes at 75, 100
    /// and 150 percent on the real ball HWND, and asserts the strip is a slim accent
    /// handle rather than a squeezed ball. Sized and placed against the actual work area.
    /// </summary>
    private void RenderDockVariants(string outputDirectory)
    {
        foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            string label = theme.ToString().ToLowerInvariant();
            Brush background = theme == ThemeMode.Light ? Brushes.WhiteSmoke : Brushes.Black;
            _themeService.Apply(theme);
            _widget.Update(FreshSampleState());
            _widget.UpdateLocalUsage(LocalAvailable());
            _widget.UpdateObservedUsage(ObservedAvailable());
            foreach (int size in new[] { 75, 100, 150 })
            {
                var ball = new FloatingBallWindow { DataContext = _widget };
                ball.AttachController(new InteractionController(new ManualDelayScheduler()));
                ball.ApplyOpacity(0.95);
                ball.ApplySize(size);
                ball.ShowActivated = false;
                ball.Show();
                double diameter = 72 * size / 100d;
                System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(ball).Handle).WorkingArea;
                double scale = MonitorService.GetScaleForWindow(ball);
                ball.MoveTo((area.Left + area.Right) / 2 - (diameter * scale / 2), area.Top + (40 * scale));
                ball.AdoptDock(BallDockSide.Left);
                double stripWidthDip = 16 * size / 100d;
                double stripHeightDip = 52 * size / 100d;
                Check($"{label}_size{size}_strip_collapses_to_slim_handle", true,
                    ball.DockSide == BallDockSide.Left && ball.CurrentPresentation == BallDockPresentation.Collapsed &&
                    Math.Abs(ball.ActualWidth - stripWidthDip) <= 1 && Math.Abs(ball.ActualHeight - stripHeightDip) <= 1);
                var accent = (FrameworkElement)ball.FindName("DockStripAccent");
                double fraction = _widget.BallRemainingFraction;
                Check($"{label}_size{size}_capsule_shell_is_slender_and_light", true,
                    Math.Abs(accent.ActualWidth - 14 * size / 100d) <= 0.6 &&
                    SampleStripPixel(ball, accent, .1, .5) is { R: >= 215, G: >= 215, B: >= 215 });
                Check($"{label}_size{size}_strip_remaining_fill_matches_yellow_quota_state", true,
                    fraction is > 0 and < 1 && _widget.Severity == QuotaSeverity.Yellow &&
                    RenderedColorMatches("QuotaAmberTextBrush", SampleStripPixel(ball, accent, .5, .8)));
                Check($"{label}_size{size}_capsule_fill_starts_at_real_remaining_boundary", true,
                    RenderedColorMatches("DockStripTrackBrush", SampleStripPixel(ball, accent, .5, (4 + 44 * (1 - fraction) - 2) / 52)) &&
                    RenderedColorMatches("QuotaAmberTextBrush", SampleStripPixel(ball, accent, .5, (4 + 44 * (1 - fraction) + 3) / 52)));
                var ballHost = (FrameworkElement)ball.FindName("BallHost");
                Check($"{label}_size{size}_strip_hides_ball_surface", Visibility.Collapsed, ballHost.Visibility);
                RenderElement((FrameworkElement)ball.Content, Path.Combine(outputDirectory, $"strip-{label}-{size}.png"), background);

                ball.ApplyDockPresentation(BallDockPresentation.Revealed);
                Check($"{label}_size{size}_reveal_restores_full_inward_ball", true,
                    ball.CurrentPresentation == BallDockPresentation.Revealed && BallHasPhysicalSize(ball, diameter) &&
                    ball.PointToScreen(new Point(0, 0)).X >= area.Left - 1 && ballHost.Visibility == Visibility.Visible);
                RenderElement((FrameworkElement)ball.Content, Path.Combine(outputDirectory, $"strip-revealed-{label}-{size}.png"), background);
                ball.Close();
            }

            // Zero/unknown retain the inset track; full retains the surrounding shell.
            StripQuotaCase("unknown", UnknownSampleState(), null);
            StripQuotaCase("full", FullSampleState(), 1);
            StripQuotaCase("zero", ZeroSampleState(), 0);

            // Every fresh tier fills the same lane in its own remaining-number color.
            StripQuotaCase("tier-green-70", BuildState(usedPercent: 30, status: RefreshStatus.Fresh), .70, QuotaSeverity.Green);
            StripQuotaCase("tier-yellow-40", BuildState(usedPercent: 60, status: RefreshStatus.Fresh), .40, QuotaSeverity.Yellow);
            StripQuotaCase("tier-red-15", BuildState(usedPercent: 85, status: RefreshStatus.Fresh), .15, QuotaSeverity.Red);
            StripQuotaCase("tier-red-14", BuildState(usedPercent: 86, status: RefreshStatus.Fresh), .14, QuotaSeverity.Red);

            void StripQuotaCase(string caseName, RefreshState state, double? expectedFraction, QuotaSeverity? expectedSeverity = null)
            {
                _widget.Update(state);
                var strip = new FloatingBallWindow { DataContext = _widget };
                strip.AttachController(new InteractionController(new ManualDelayScheduler()));
                strip.ApplyOpacity(0.95);
                strip.ShowActivated = false;
                strip.Show();
                System.Drawing.Rectangle caseArea = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(strip).Handle).WorkingArea;
                double caseScale = MonitorService.GetScaleForWindow(strip);
                strip.MoveTo((caseArea.Left + caseArea.Right) / 2 - (72 * caseScale / 2), caseArea.Top + (40 * caseScale));
                strip.AdoptDock(BallDockSide.Left);
                var caseAccent = (FrameworkElement)strip.FindName("DockStripAccent");
                double fraction = _widget.BallRemainingFraction;
                if (expectedFraction is null || expectedFraction == 0)
                {
                    // No remaining share at all: the lane must stay the plain track.
                    Check($"{label}_strip_{caseName}_quota_shows_track_only", true, fraction == 0 &&
                        RenderedColorMatches("DockStripTrackBrush", SampleStripPixel(strip, caseAccent, 0.5, 0.15)) &&
                        RenderedColorMatches("DockStripTrackBrush", SampleStripPixel(strip, caseAccent, 0.5, 0.9)));
                }
                else
                {
                    string fillKey = RemainingValueThemeKey(_widget.Severity);
                    // The 0.15 lane sample sits above the fill boundary for every partial share.
                    string topKey = (4 + 44 * (1 - expectedFraction.Value)) / 52 <= 0.15 ? fillKey : "DockStripTrackBrush";
                    Check($"{label}_strip_{caseName}_quota_fills_the_remaining_share", true,
                        (expectedSeverity is null || _widget.Severity == expectedSeverity) &&
                        Math.Abs(fraction - expectedFraction.Value) < 0.0001 &&
                        RenderedColorMatches(fillKey, SampleStripPixel(strip, caseAccent, 0.5, 0.9)) &&
                        RenderedColorMatches(topKey, SampleStripPixel(strip, caseAccent, 0.5, 0.15)) &&
                        SampleStripPixel(strip, caseAccent, .1, .5) is { R: >= 215, G: >= 215, B: >= 215 });
                }
                RenderElement((FrameworkElement)strip.Content, Path.Combine(outputDirectory, $"strip-{caseName}-{label}.png"), background);
                strip.Close();
            }
        }

        _themeService.Apply(ThemeMode.Light);
        _widget.Update(FreshSampleState());
    }

    private static bool BallHasPhysicalSize(FloatingBallWindow ball, double dipSize)
    {
        nint handle = new System.Windows.Interop.WindowInteropHelper(ball).Handle;
        if (handle == 0 || !GetNativeWindowRect(handle, out NativeRect rect))
        {
            return false;
        }

        double scale = MonitorService.GetScaleForWindow(ball);
        return Math.Abs(rect.Right - rect.Left - dipSize * scale) <= 1 && Math.Abs(rect.Bottom - rect.Top - dipSize * scale) <= 1 &&
            Math.Abs(ball.ActualWidth - dipSize) * scale <= 1 && Math.Abs(ball.ActualHeight - dipSize) * scale <= 1;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetNativeWindowRect(nint hwnd, out NativeRect rect);

    private async Task VerifyPanelSizingRendersAsync(string outputDirectory, DetailsWindow details)
    {
        details.Show();
        foreach (ThemeMode theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            string label = theme.ToString().ToLowerInvariant();
            Brush background = theme == ThemeMode.Light ? Brushes.WhiteSmoke : Brushes.Black;
            _themeService.Apply(theme);
            _widget.UpdateSettings(WidgetSettings.Default);
            _widget.Update(FreshSampleState());
            _widget.UpdateLocalUsage(LocalAvailable());
            _widget.UpdateObservedUsage(ObservedAvailable());
            await FlushResourceChangesAsync(details);
            double normal = details.Height;
            CheckDetailsHintPadding(details, label + "_details_normal_hint_bottom_padding");
            CheckDetailsScrollSurface(details, label + "_details_normal");
            RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-content-normal.png"), background);
            _widget.UpdateSettings(WidgetSettings.Default with { ShowRemaining = false, ShowResetTime = false, ShowTodayUsage = false });
            await FlushResourceChangesAsync(details);
            Check(label + "_details_hidden_cards_shorten_panel", true, details.Height < normal - 100);
            CheckDetailsHintPadding(details, label + "_details_hidden_hint_bottom_padding");
            RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-content-hidden.png"), background);
            _widget.UpdateSettings(WidgetSettings.Default);
            _widget.Update(UnknownSampleState());
            await FlushResourceChangesAsync(details);
            Check(label + "_details_no_quota_shortens_panel", true, details.Height < normal);
            CheckDetailsHintPadding(details, label + "_details_no_quota_hint_bottom_padding");
            RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-content-no-quota.png"), background);
            _widget.Update(FreshSampleState() with { StatusText = string.Concat(Enumerable.Repeat("验证长状态说明，内容换行且保持可滚动。", 30)) });
            _widget.UpdateLocalUsage(LocalLongValues());
            _widget.UpdateObservedUsage(ObservedPartialLong());
            await FlushResourceChangesAsync(details);
            MonitorService.SizePanelToWorkArea(details, 374);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            details.UpdateLayout();
            var scroll = (System.Windows.Controls.ScrollViewer)details.FindName("PanelScroll");
            scroll.ScrollToTop();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            details.UpdateLayout();
            Check(label + "_details_long_content_capped_to_work_area_and_scrolls", true, details.Height == 350 && scroll.ScrollableHeight > 0 && scroll.ScrollableWidth <= 1);
            CheckDetailsScrollSurface(details, label + "_details_long");
            RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-content-long.png"), background);
            scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(label + "_details_hidden_track_wheel_access", true, scroll.VerticalOffset > 0);
            double wheelOffset = scroll.VerticalOffset;
            scroll.Focus();
            scroll.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(scroll), Environment.TickCount, System.Windows.Input.Key.PageDown)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(label + "_details_hidden_track_keyboard_access", true, scroll.VerticalOffset > wheelOffset);
            scroll.ScrollToEnd();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            details.UpdateLayout();
            CheckDetailsHintPadding(details, label + "_details_scrolled_hint_bottom_padding");
            RenderScreenshot(details, Path.Combine(outputDirectory, $"details-{label}-content-long-bottom.png"), background);
        }
        _widget.UpdateSettings(WidgetSettings.Default);
        _widget.Update(FreshSampleState());
        _widget.UpdateLocalUsage(LocalAvailable());
        _widget.UpdateObservedUsage(ObservedAvailable());
        await FlushResourceChangesAsync(details);
        ((System.Windows.Controls.ScrollViewer)details.FindName("PanelScroll")).ScrollToTop();
    }

    private void CheckDetailsScrollSurface(DetailsWindow details, string name)
    {
        var scroll = (System.Windows.Controls.ScrollViewer)details.FindName("PanelScroll");
        var presenter = FindVisual<System.Windows.Controls.ScrollContentPresenter>(scroll);
        Check(name + "_no_visible_scrollbars_or_reserved_channel", true, !FindVisuals<System.Windows.Controls.Primitives.ScrollBar>(scroll).Any(bar => bar.IsVisible) && presenter is not null && Math.Abs(presenter.ActualWidth - scroll.ActualWidth) <= 1);
        _checks[^1]["reservedWidthDip"] = presenter is null ? null : Math.Round(scroll.ActualWidth - presenter.ActualWidth, 2);
    }

    private void CheckDetailsHintPadding(DetailsWindow details, string name)
    {
        var hint = FindVisuals<System.Windows.Controls.TextBlock>(details).First(block =>
            System.Windows.Data.BindingOperations.GetBinding(block, System.Windows.Controls.TextBlock.TextProperty)?.Path.Path == "HintText");
        var root = (FrameworkElement)details.Content;
        double bottom = hint.TransformToAncestor(root).Transform(new Point(0, hint.ActualHeight)).Y;
        Check(name, true, root.ActualHeight - bottom is >= 12 and <= 18);
        _checks[^1]["bottomGapDip"] = Math.Round(root.ActualHeight - bottom, 2);
    }

    private void RenderScreenshot(Window window, string path, Brush background) =>
        RenderScreenshot(window, path, background, 0, 0);

    private void RenderScreenshot(Window window, string path, Brush background, double width, double height)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        if (width > 0 && height > 0)
        {
            Layout(window, width, height);
        }

        RenderElement(root, path, background);
    }

    private void RenderElement(FrameworkElement root, string path, Brush background)
    {
        double pixelWidth = Math.Max(1, Math.Ceiling(root.ActualWidth));
        double pixelHeight = Math.Max(1, Math.Ceiling(root.ActualHeight));
        const double bannerHeight = 18;
        double canvasHeight = pixelHeight + bannerHeight;

        var windowBitmap = new RenderTargetBitmap((int)pixelWidth, (int)pixelHeight, 96, 96, PixelFormats.Pbgra32);
        windowBitmap.Render(root);

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, pixelWidth, canvasHeight));
            context.DrawImage(windowBitmap, new Rect(0, 0, pixelWidth, pixelHeight));

            // The verification banner sits below the content so the artifact stays inspectable.
            var bannerBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x11, 0x11, 0x11));
            context.DrawRectangle(bannerBrush, null, new Rect(0, pixelHeight, pixelWidth, bannerHeight));
            var text = new FormattedText(
                SampleBanner,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                Brushes.White,
                96);
            context.DrawText(text, new Point(3, pixelHeight + 2));
        }

        var composite = new RenderTargetBitmap((int)pixelWidth, (int)canvasHeight, 96, 96, PixelFormats.Pbgra32);
        composite.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composite));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
        _screenshots.Add(Path.GetFileName(path));
    }

    private static LocalUsageSnapshot LocalAvailable() => new(
        DateOnly.FromDateTime(DateTime.Now),
        3,
        123456,
        LocalUsageCoverage.Available,
        null,
        DateTimeOffset.Now,
        4,
        0,
        4096);

    private static LocalUsageSnapshot LocalPartial() => new(
        DateOnly.FromDateTime(DateTime.Now),
        3,
        123456,
        LocalUsageCoverage.Partial,
        "部分记录的增量无法完整归属到今天，统计可能不完整。",
        DateTimeOffset.Now,
        4,
        1,
        4096);

    private static LocalUsageSnapshot LocalLongValues() => new(
        DateOnly.FromDateTime(DateTime.Now),
        128,
        12345678901L,
        LocalUsageCoverage.Partial,
        "本机记录较多，本轮只读取了部分文件，统计可能不完整。",
        DateTimeOffset.Now,
        128,
        9,
        96L * 1024 * 1024);

    private static LocalUsageSnapshot LocalUnavailable() => LocalUsageSnapshot.Unavailable(
        DateOnly.FromDateTime(DateTime.Now),
        DateTimeOffset.Now,
        "未找到本机 Codex 记录目录（sessions / archived_sessions）。");

    private static ObservedUsageSnapshot ObservedWaiting() => new(
        DateOnly.FromDateTime(DateTime.Now),
        ObservedUsageState.Waiting,
        [new ObservedPeriodEstimate("primary", 300, ObservedPeriodState.Waiting, 0, 1, DateTimeOffset.Now, DateTimeOffset.Now, false)]);

    private static ObservedUsageSnapshot ObservedAvailable() => new(
        DateOnly.FromDateTime(DateTime.Now),
        ObservedUsageState.Available,
        [
            new ObservedPeriodEstimate("primary", 300, ObservedPeriodState.Available, 3.5, 4, DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now, false),
            new ObservedPeriodEstimate("secondary", 10080, ObservedPeriodState.Available, 0, 2, DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now, false),
        ]);

    private static ObservedUsageSnapshot ObservedPartialLong() => new(
        DateOnly.FromDateTime(DateTime.Now),
        ObservedUsageState.Partial,
        [
            new ObservedPeriodEstimate("primary", 300, ObservedPeriodState.Partial, 148.5, 12, DateTimeOffset.Now.AddHours(-2), DateTimeOffset.Now, true),
            new ObservedPeriodEstimate("secondary", null, ObservedPeriodState.Unavailable, 0, 0, null, null, false),
        ]);

    private static RefreshState FreshSampleState()
    {
        var primary = new QuotaWindow("primary", 300, 54, 46, DateTimeOffset.Now.AddHours(2));
        var secondary = new QuotaWindow("secondary", 10080, 8, 92, DateTimeOffset.Now.AddDays(5));
        var main = new QuotaBucket("codex", null, [primary, secondary], false, "12.5", true, false, null) { PlanType = "plus" };
        var auxiliary = new QuotaBucket("gpt-4o", "辅助桶", [new QuotaWindow("primary", 1440, 99, 1, DateTimeOffset.Now.AddHours(6))], false, null, null, null, null);
        var snapshot = new QuotaSnapshot("codex", "plus", [main, auxiliary], DateTimeOffset.Now);
        var daily = DailyUsageResult.Missing(DateOnly.FromDateTime(DateTime.Now));

        return new RefreshState(
            RefreshStatus.Fresh,
            snapshot,
            daily,
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddSeconds(120),
            null);
    }

    private static RefreshState ZeroSampleState() =>
        BuildState(usedPercent: 100, status: RefreshStatus.Fresh);

    private static RefreshState FullSampleState() =>
        BuildState(usedPercent: 0, status: RefreshStatus.Fresh);

    private static RefreshState StaleSampleState() =>
        BuildState(usedPercent: 54, status: RefreshStatus.Stale, statusText: "额度数据已过期，等待下一次刷新。");

    private static RefreshState UnknownSampleState() => new(
        RefreshStatus.Error,
        null,
        null,
        DateTimeOffset.Now,
        null,
        DateTimeOffset.Now.AddMinutes(2),
        "无法获取官方额度。");

    private static RefreshState BuildState(int usedPercent, RefreshStatus status, string? statusText = null)
    {
        var window = new QuotaWindow("primary", 300, usedPercent, 100 - usedPercent, DateTimeOffset.Now.AddHours(1));
        var bucket = new QuotaBucket("codex", null, [window], false, null, false, false, null) { PlanType = "plus" };
        var snapshot = new QuotaSnapshot("codex", "plus", [bucket], DateTimeOffset.Now);

        return new RefreshState(
            status,
            snapshot,
            null,
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            DateTimeOffset.Now.AddSeconds(120),
            statusText);
    }

    /// <summary>Records scheduled callbacks so interaction wiring can be verified without real delays.</summary>
    private sealed class ManualDelayScheduler : IDelayScheduler
    {
        private readonly List<(Action Callback, bool Disposed)> _handles = [];

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            int index = _handles.Count;
            _handles.Add((callback, false));
            return new Handle(() => _handles[index] = (callback, true));
        }

        public void FireAll()
        {
            for (int i = 0; i < _handles.Count; i++)
            {
                (Action callback, bool disposed) = _handles[i];
                if (!disposed)
                {
                    _handles[i] = (callback, true);
                    callback();
                }
            }
        }

        private sealed class Handle : IDisposable
        {
            private readonly Action _dispose;

            public Handle(Action dispose) => _dispose = dispose;

            public void Dispose() => _dispose();
        }
    }
}
