# Changelog

Bu dosya Keep a Changelog prensibine gore tutulur.

## [0.3.0] - 2026-06-20

### Added

- Manual butunluk dogrulama: servis, API endpoint ve History UI aksiyonu
- Butunluk durumunun kalici alanlarla DB'ye yazilmasi ve UI'de goruntulenmesi
- Startup'ta port fallback (5000 doluysa uygun baska porta gecis)
- BackupHistories tablosuna butunluk kolonlari icin startup schema upgrade
- Jobs ekraninda kullanici dostu zamanlama arayuzu
- Jobs ekraninda cron tabanli sonraki calisma zamanlari onizlemesi

### Changed

- Non-Windows platformlarda backup imaj uzantisi ISO olarak secilir
- ISO olusturmada kilitli/okunamayan dosyalar warning ile atlanir

### Fixed

- macOS Photos lock dosyalari nedeniyle tum backup gorevinin dusmesi engellendi
