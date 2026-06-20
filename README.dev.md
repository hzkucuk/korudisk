# KoruDisk Development Notes

Bu dosya, gelistirme akisini ve teknik operasyon notlarini tutmak icin kullanilir.

## Standart Gelistirme Akisi

```bash
dotnet build src/KoruDisk.slnx
dotnet test src/KoruDisk.Tests/KoruDisk.Tests.csproj
dotnet run --project src/KoruDisk.Web/KoruDisk.Web.csproj
```

## Cron / Scheduler Notlari

- Cron parser: `Cronos`
- Standart format: `dakika saat gun ay haftanin-gunu`
- UI tarafinda cron teknik alan olarak gizlenmez ama ileri seviye alanda tutulur.
- Jobs ekraninda sonraki calistirma zamanlari onizlenir.

## Backup Deneme Notu Formati

Asagidaki formatla ilerleyin:

```md
## YYYY-MM-DD HH:mm
- Change: Yapilan degisiklik
- Verify: Calistirilan komut veya senaryo
- Result: Basarili/Basarisiz + ozet
- Follow-up: Sonraki adim
```

## Release Hazirlik Kontrolu

- [ ] `VersionPrefix` guncellendi
- [ ] `CHANGELOG.md` guncellendi
- [ ] Build/Test gecti
- [ ] Etiket olusturuldu (`vX.Y.Z`)
