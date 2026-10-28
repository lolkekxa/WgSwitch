# Цифровая подпись

Без подписи Windows SmartScreen показывает на установщике «Windows защитила ваш компьютер» и «Неизвестный издатель».
Сертификат для подписи кода стоит от ~$200 в год. Для open source есть бесплатный путь: **SignPath Foundation**
подписывает сборки своим сертификатом, если они собраны из открытого кода на CI.

Издателем в свойствах файла будет «SignPath Foundation», а не LKXA. Это нормально: так подписаны многие открытые проекты.

## Подходит ли WgSwitch

| Требование SignPath Foundation | WgSwitch |
|---|---|
| OSI-лицензия, весь код открыт | ✓ MIT, репозиторий публичный |
| Нет закрытых компонентов внутри сборки | ✓ только .NET и сам код; WireGuard ставится официальным установщиком отдельно |
| Сборка на CI из исходников, без ручных шагов | ✓ GitHub Actions, `.github/workflows/build.yml` |
| Проект уже опубликован и им пользуются | ✓ релизы на GitHub |
| Описанная политика подписи на странице проекта | раздел ниже — добавить в README при подаче |
| Двухфакторная аутентификация у всех, кто пушит | включить на GitHub, если ещё нет |

## Шаги

1. **Заявка.** Форма на [signpath.org](https://signpath.org) → «Apply». Указать репозиторий, лицензию MIT,
   что подписывается (`WgSwitch.exe` и `WgSwitch-Setup-*.exe`) и ссылку на workflow. Рассмотрение занимает от
   нескольких дней до нескольких недель.
2. **После одобрения** в кабинете SignPath появятся организация и проект. Там же:
   - установить GitHub App SignPath на репозиторий (он проверяет, что артефакт собран именно этим workflow);
   - создать API-токен пользователя CI.
3. **Секреты репозитория** (Settings → Secrets and variables → Actions):
   - `SIGNPATH_API_TOKEN` — токен из шага 2;
   - `SIGNPATH_ORGANIZATION_ID` — ID организации из кабинета.
4. **Политика подписи в README** — короткий раздел, его SignPath требует на странице проекта:

   ```markdown
   ## Политика подписи

   Бесплатная подпись кода — [SignPath.io](https://about.signpath.io), сертификат — [SignPath Foundation](https://signpath.org).
   Подписываются только сборки GitHub Actions из этого репозитория. Релизы одобряет автор: LKXA.
   Программа не передаёт данные никуда, кроме запроса последней версии к api.github.com (выключается в настройках).
   ```

5. **Шаги в CI.** Подписать нужно дважды: сначала `WgSwitch.exe` (до упаковки в установщик, иначе внутри
   останется неподписанный файл), затем сам установщик. В `build.yml` после шага «Publish for installer»:

   ```yaml
   - name: Upload unsigned app
     id: unsigned-app
     uses: actions/upload-artifact@v4
     with:
       name: unsigned-app
       path: |
         out/setup/WgSwitch.exe
         out/full/WgSwitch.exe
         out/lite/WgSwitch.exe
       retention-days: 1

   - name: Sign app
     uses: signpath/github-action-submit-signing-request@v1
     with:
       api-token: ${{ secrets.SIGNPATH_API_TOKEN }}
       organization-id: ${{ secrets.SIGNPATH_ORGANIZATION_ID }}
       project-slug: WgSwitch
       signing-policy-slug: release-signing
       artifact-configuration-slug: app
       github-artifact-id: ${{ steps.unsigned-app.outputs.artifact-id }}
       wait-for-completion: true
       output-artifact-directory: out/signed
   ```

   Потом разложить подписанные `WgSwitch.exe` обратно по `out/setup`, `out/full`, `out/lite`, собрать установщик
   как сейчас и повторить ту же пару шагов для `WgSwitch-Setup-*.exe` (artifact-configuration `setup`).
   Для сборок `dev` подпись можно не делать — только для тегов `v*` (`if: startsWith(github.ref, 'refs/tags/v')`).

   Слаги (`project-slug`, `signing-policy-slug`, `artifact-configuration-slug`) задаются в кабинете SignPath —
   подставить те, что получатся там.

6. **Проверка.** Свойства файла → «Цифровые подписи»: SignPath Foundation, подпись действительна.
   SmartScreen доверяет не сразу: репутация копится по мере скачиваний, но «Неизвестный издатель» пропадает сразу.

## Квота артефактов

Подпись идёт через артефакт GitHub Actions. Раньше сборка упиралась в квоту хранилища артефактов,
поэтому у временных артефактов стоит `retention-days: 1`. Если квота снова кончится, загрузка упадёт —
старые артефакты можно удалить в Actions → Caches/Artifacts.
