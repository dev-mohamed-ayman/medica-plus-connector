# Pharmacy Sync

برنامج ديسكتوب (exe واحد) لربط برامج الصيدليات مع الإيكومرس — بديل سكربتات PowerShell.

## نسختين حسب الويندوز
| الويندوز | الملف | محتاج يتسطب حاجة؟ |
|---|---|---|
| Windows 10 / 11 | `dist\PharmacySync.exe` | لا (.NET 4.8 موجود في الويندوز) |
| Windows 7 (بـ SP1 أو من غيره) | `dist\PharmacySync-Win7.exe` | لا (.NET 3.5 موجود في كل ويندوز 7) |

نسخة ويندوز 7 بتعمل اتصال HTTPS (TLS 1.2) بنفسها عن طريق مكتبة BouncyCastle المدمجة جوه الـ exe،
ومعاها شهادات الـ Root (Let's Encrypt / DigiCert / Google / Amazon / Sectigo / GlobalSign / GoDaddy) في `src\Win7\roots.pem`،
فمش محتاجة تحديثات ويندوز ولا شهادات على الجهاز.
لو عميل عنده API على شركة شهادات مش في الملف، ضيف الـ Root بتاعها في `roots.pem` واعمل build.

## التشغيل عند العميل
1. انسخ النسخة المناسبة لجهاز العميل وشغلها (هتطلب صلاحية Administrator). البرنامج بينسخ نفسه لـ `Program Files\PharmacySync\PharmacySync.exe` في الحالتين.
2. سجل دخول.
3. **ربط عميل جديد** ← اختر البرنامج (Estock) ← فرع واحد / متعدد الفروع ← بيانات SQL Server (اختبار الاتصال) ← رابط ومفتاح الـ API ← **حفظ**.
4. عند الحفظ البرنامج:
   - يحفظ الإعدادات مشفرة في `C:\ProgramData\PharmacySync` (Administrators و SYSTEM بس اللي يقدروا يوصلوا لها).
   - ينسخ نفسه إلى `C:\Program Files\PharmacySync` ويعمل اختصار على سطح المكتب.
   - ينشئ مهمة مجدولة `\PharmacySync\Sync-<id>` تشتغل بحساب SYSTEM كل X دقيقة (بدون أي نافذة).
5. لو على الجهاز مهمة السكربت القديمة `PharmacyInventorySync` هيظهر تنبيه وزرار لحذفها (عشان البيانات ماتتبعتش مرتين).

## أزرار كل ربط
| الزر | الوظيفة |
|---|---|
| مزامنة الآن | مزامنة فعلية مع سجل مباشر |
| تجربة بدون إرسال | يقرأ البيانات ويجهز الدفعات بدون ما يبعت أي حاجة |
| تفعيل / إيقاف الجدولة | ينشئ / يحذف المهمة المجدولة |
| تشغيل المهمة المجدولة | يشغل المهمة بحساب SYSTEM للتأكد إنها شغالة فعلاً |
| السجل | آخر مزامنة / السابقة / آخر تجربة |

## البناء (Build)
لا يحتاج أي SDK — بيستخدم compiler الـ .NET Framework الموجود في ويندوز:
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -AdminUser admin -AdminPassword "كلمة-سر-قوية"
```
اسم المستخدم وكلمة المرور بيتخزنوا جوه الـ exe كـ hash (PBKDF2) — الباسورد نفسه مش متخزن.
بعد أول build تقدر تشغل `build.ps1` من غير باراميترز وهيستخدم نفس البيانات.

الـ build بيطلع النسختين مع بعض. لنسخة واحدة بس: `-Target Modern` أو `-Target Win7`.
نسخة ويندوز 7 محتاجة .NET Framework 3.5 يكون مفعل على جهاز الـ build (Windows Features).

قواعد الكود عشان نسخة ويندوز 7 تفضل شغالة (.NET 3.5):
- ماتستخدمش `dynamic` (استخدم `Com.Call/Get/Set`)، ولا `string.IsNullOrWhiteSpace` (استخدم `StrUtil.IsBlank`)، ولا `StringBuilder.Clear`، ولا `Task`/`async`.
- أي حاجة خاصة بنسخة معينة تتحط جوه `#if NET35`.

## سطر الأوامر
```
PharmacySync.exe --run <id>       مزامنة بدون واجهة (المستخدم في المهمة المجدولة)
PharmacySync.exe --dry-run <id>   تجربة بدون واجهة
```

## Pharma Fly (قاعدة pfx)
| الحقل | المصدر |
|---|---|
| code | `Items.ID` |
| name_en / name_ar | `Items.name` (إنجليزي ← name_en، عربي ← name_ar، مختلط ← الاتنين) |
| price | `Items.SalesPrice` |
| quantity | مجموع `Work` (Packs + Units ÷ Parts) في كل المخازن **ما عدا المخازن المخفية** (هالك/منتهي)، مقرب لأقل رقم صحيح |
| international_barcode | أول باركود في `CrossRefrences` |
| الأصناف | الفعالة وغير المؤرشفة (`IsActive`/`Archived`) |

- **فرع واحد:** كل الأصناف (الرصيد 0 لو مفيش) في دفعات 500 مع 500ms — زي Estock بالظبط.
- **متعدد الفروع:** كل فرع Pharma Fly ليه قاعدة بيانات منفصلة، فالبرنامج بيبعت فرع القاعدة دي (`BranchIdentity` ← `Branches`) بالأصناف اللي رصيدها > 0 في دفعات 1000.
  - الأفضل: تسطيب البرنامج على سيرفر كل فرع.
  - أو تفعيل خيار "قراءة مخزون باقي الفروع من سيرفراتها" — هيتصل بكل فرع ببيانات الاتصال المسجلة في جدول `Branches` (IP / DB / user / password). لو فرع مش متاح هيتسجل في السجل والحالة تبقى "جزئية" وباقي الفروع تتبعت عادي.
  - كود الفرع = `Branches.ID`، العنوان والتليفون من `Settings` في قاعدة كل فرع.

## إضافة برنامج جديد (BConnect)
1. اعمل class جديد يطبق `ISyncJob` في `src\SyncEngine.cs` (الاستعلامات + بناء الـ payload).
2. سجله في `SyncJobs.Create`.
3. خلي `Available = true` للبرنامج في `SoftwareInfo.All` (`src\Models.cs`).
4. (اختياري) أضف فحص الجداول الخاص بيه بدل `EstockInspector`.

## الملفات
- `src\SyncEngine.cs` — منطق المزامنة (نقل حرفي للسكربتين: نفس الاستعلامات، 500 منتج للفرع الواحد مع 500ms بين الدفعات، 1000 منتج لكل فرع في المتعدد).
- `src\TaskService.cs` — Task Scheduler + التثبيت.
- `src\Storage.cs` — حفظ الإعدادات المشفرة (DPAPI) والحالة.
- `src\UI\*` — الواجهات.
- `src\Win7\Win7Http.cs` — HTTPS لنسخة ويندوز 7 (BouncyCastle TLS 1.2 + فحص الشهادة واسم السيرفر).
- `src\Win7\roots.pem` — شهادات الـ Root المدمجة في نسخة ويندوز 7.
- `lib\BouncyCastle.Crypto.dll` — مكتبة BouncyCastle 1.8.9 (بتتدمج جوه نسخة ويندوز 7).
