# 🚀 GitDeploy Pro - Development Plan

## ✅ Completed Steps
- [x] Step 1: Base WPF window (simple UI)
- [x] Step 2: Setup MahApps.Metro theme (Dark.Blue)

## 📋 Development Roadmap

### Phase 1: Main Window & Navigation (مرحله 1: پنجره اصلی و ناوبری)
- [ ] Step 3: Create modern MetroWindow with sidebar
- [ ] Step 4: Add navigation menu items (Dashboard, Deploy, History, Settings)
- [ ] Step 5: Implement page navigation system

### Phase 2: Dashboard Page (مرحله 2: صفحه داشبورد)
- [ ] Step 6: Create Dashboard page with project info
- [ ] Step 7: Add Git status display (current branch, commits)
- [ ] Step 8: Show quick stats (files changed, last deployment)

### Phase 3: Deploy Page (مرحله 3: صفحه دیپلوی)
- [ ] Step 9: Create Deploy page with branch selection
- [ ] Step 10: Add file list view (changed files)
- [ ] Step 11: Implement deploy button with progress bar
- [ ] Step 12: Show real-time deployment logs

### Phase 4: Git Integration (مرحله 4: یکپارچه‌سازی Git)
- [ ] Step 13: Add Git service for repo detection
- [ ] Step 14: Implement auto-init Git if not exists
- [ ] Step 15: Add branch management (create, switch, sync)
- [ ] Step 16: Add commit & push functionality

### Phase 5: Deployment Service (مرحله 5: سرویس دیپلوی)
- [ ] Step 17: Implement FTP upload service
- [ ] Step 18: Implement SSH/SFTP upload service
- [ ] Step 19: Add file diff detection (git diff)
- [ ] Step 20: Implement exclude patterns

### Phase 6: History & Rollback (مرحله 6: تاریخچه و بازگشت)
- [ ] Step 21: Create History page with deployment list
- [ ] Step 22: Store deployment records in JSON
- [ ] Step 23: Implement rollback functionality
- [ ] Step 24: Show deployment details (files, time, status)

### Phase 7: Settings & Config (مرحله 7: تنظیمات و پیکربندی)
- [ ] Step 25: Create Settings page
- [ ] Step 26: Add config manager (FTP/SSH settings)
- [ ] Step 27: Implement exclude patterns editor
- [ ] Step 28: Save/Load config from JSON

### Phase 8: Final Touches (مرحله 8: لمسات نهایی)
- [ ] Step 29: Add about page with version info
- [ ] Step 30: Implement notifications/alerts
- [ ] Step 31: Add loading indicators
- [ ] Step 32: Create standalone .exe build

## 🎯 Current Task
**Step 3**: Creating modern MetroWindow with sidebar navigation

## 🔧 Tech Stack
- .NET 8.0 (WPF)
- MahApps.Metro 2.4.11 (Modern UI)
- Newtonsoft.Json (Config management)
- System.Management.Automation (PowerShell for Git)

## 📝 Notes
- هر مرحله بعد از تکمیل تست می‌شه
- اگر مشکلی پیش اومد، برمی‌گردیم و اصلاح می‌کنیم
- UI باید مدرن و کاربرپسند باشه
- همه چیز باید RTL و فارسی باشه
