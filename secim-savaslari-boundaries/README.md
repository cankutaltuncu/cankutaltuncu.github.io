# Seçim Savaşları sınır verisi

Bu yayın, Seçim Savaşları için kullanılan Türkiye il ve ilçe sınır verisinin indirilebilir kopyasını, işlenmiş Unity çıktısını ve dönüşüm bilgisini sağlar.

## İndir

- [İşlenmiş Unity varlıkları (ZIP)](data/secim-savaslari-processed-boundaries.zip)
- [ADM1 kaynak GeoJSON — 81 il](data/source/geoBoundaries-TUR-ADM1_simplified.geojson)
- [ADM2 kaynak GeoJSON — 973 ilçe](data/source/geoBoundaries-TUR-ADM2_simplified.geojson)
- [Dönüşüm algoritması](transform/TurkeyGeoJsonBatchImporter.cs)
- [Üretim/doğrulama raporu](transform/turkey-map-import.txt)

## Kaynak ve lisans

ADM2 kaynak verisi geoBoundaries tarafından standartlaştırılan OpenStreetMap / OSM Boundaries verisine dayanır ve Open Data Commons Open Database License 1.0 (ODbL) kapsamındadır. ADM1 kaynak metadata lisansı CC BY-SA 2.0'dır.

Administrative boundaries © OpenStreetMap contributors, ODbL 1.0; standardized by geoBoundaries (www.geoboundaries.org).

## Dönüşüm

Sabit veri sürümü `9469f09` kullanıldı. GeoJSON verisi 81 il ve 973 ilçe olarak doğrulandı; ilçe halkaları Douglas-Peucker yöntemiyle `0.0015` derece toleransta sadeleştirildi, il merkezi çevresinde normalize edilerek Unity koordinatlarına `3` ölçeğiyle dönüştürüldü. İlçe-il eşleşmesi güvenli iç nokta testiyle yapıldı; komşuluklar sadeleştirme öncesindeki coğrafi koordinatlardan üretildi.

İşlenmiş çıktı ZIP paketi 81 `ProvinceData` ve 973 `DistrictData` Unity varlığı içerir. Tam, makinece üretim kaydı `transform/turkey-map-import.txt` dosyasındadır.

Kaynak kontrolleri:

- ADM1 SHA-256: `94f64008c50e1953a56eb9dc1fb7367944d2290c6da5976a4f31e7eca35b3117`
- ADM2 SHA-256: `17af9a6bff9bed53390e49cbbf66569e0d296f134cd4528e1dc3c0d204e358f3`
- İşlenmiş ZIP SHA-256: `ce59637a0eaf2a75e90271b691fef2509f35297a94707c78611c3c831bc25252`

Bu sayfa hukuki görüş değildir. Türetilmiş veri bu yayınla erişilebilir tutulur; ODbL kapsamındaki kullanımda uygun atıf ve paylaşım koşulları korunmalıdır.
