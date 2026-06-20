# KoruDisk

KoruDisk, .NET 10 tabanli bir yedekleme uygulamasidir. Blazor Server arayuzu ile gorev tanimi, zamanlama, surumleme, hedef dagitimi ve geri yukleme akisi saglar.

## Ozellikler

- Tam ve artimli yedekleme gorevleri
- Yerel/SFTP/FTP/Google Drive hedeflerine dagitim
- Cron tabanli zamanlama + kullanici dostu zamanlama arayuzu
- Yedek olusturma sonrasi butunluk dogrulamasi
- Manuel butunluk dogrulama endpointi ve History ekraninda durum takibi
- macOS/Linux ortamlarinda ISO akisi (Windows disinda VHD yerine)

## Teknoloji Yigini

- .NET 10
- ASP.NET Core Blazor Server
- EF Core + SQLite
- Cronos
- DiscUtils

## Hizli Baslangic (Install + Run)

### 1) Gereksinimler

- .NET SDK 10.0+
- macOS, Linux veya Windows

### 2) Repo klonla

```bash
git clone https://github.com/<your-user>/korudisk.git
cd korudisk
```

### 3) Derle

```bash
dotnet build src/KoruDisk.slnx
```

### 4) Testleri calistir

```bash
dotnet test src/KoruDisk.Tests/KoruDisk.Tests.csproj
```

### 5) Uygulamayi calistir

```bash
dotnet run --project src/KoruDisk.Web/KoruDisk.Web.csproj
```

Not: Uygulama varsayilan port doluysa otomatik olarak uygun bir porta gecer.

## Surumleme Politikasi

Bu repoda merkezi surumleme `Directory.Build.props` ile yonetilir.

- `VersionPrefix`: genel uygulama surumu (simdi: `0.3.0`)
- `AssemblyVersion` / `FileVersion`: sabit ve derleme dostu format
- `InformationalVersion`: yayin etiketi

Surum artirirken:

1. `Directory.Build.props` icindeki `VersionPrefix` degerini guncelleyin.
2. `CHANGELOG.md` dosyasina yeni surum notlarini ekleyin.
3. Etiketlemek icin git tag kullanin: `git tag v0.3.0`.

## Gelistirme Loglama

Gelistirme gecmisi iki yerde tutulur:

- `CHANGELOG.md`: Surum bazli degisiklik ozeti
- `README.dev.md`: Gunluk gelistirme/operasyon notlari, yerel akislar, kararlar

Log format detaylari icin `README.dev.md` dosyasina bakin.

## GitHub'da Public Repo Olarak Yayinlama

Asagidaki adimlar repo olusturma icindir:

```bash
git init
git add .
git commit -m "chore: bootstrap korudisk repository"
git branch -M main
gh repo create korudisk --public --source=. --remote=origin --push
```

`gh` komutu yoksa GitHub UI uzerinden yeni public repo acip su komutlari calistirin:

```bash
git remote add origin https://github.com/<your-user>/korudisk.git
git push -u origin main
```
