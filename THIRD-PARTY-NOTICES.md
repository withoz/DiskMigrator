# 제3자 오픈소스 고지 (Third-Party Notices)

DiskMigrator는 다음 오픈소스 구성요소를 사용합니다. 각 구성요소는 아래에 명시된
라이선스로 배포되며, 모두 허용적(permissive) 라이선스로 이 프로젝트의 MIT 배포와
호환됩니다.

self-contained 단일 실행파일로 배포할 경우, .NET 8 런타임과 아래 라이브러리들이
실행파일 안에 포함됩니다.

---

## MIT 라이선스 구성요소

- **CommunityToolkit.Mvvm** © .NET Foundation and Contributors
- **System.Management** © Microsoft Corporation
- **System.CodeDom** © Microsoft Corporation
- **Microsoft.Extensions.Logging** (및 관련 Microsoft.Extensions.* 패키지) © Microsoft Corporation
- **.NET 8 런타임 / 라이브러리** © Microsoft Corporation

MIT 라이선스 전문:

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## Apache License 2.0 구성요소

- **Serilog** © Serilog Contributors
- **Serilog.Extensions.Logging** © Serilog Contributors
- **Serilog.Sinks.Console** © Serilog Contributors
- **Serilog.Sinks.File** © Serilog Contributors
- **AlphaVSS** © AlphaVSS Contributors
- **AlphaVSS.Native.NetCore** © AlphaVSS Contributors

이들 구성요소는 Apache License, Version 2.0으로 배포됩니다. Apache-2.0은 배포 시
저작권 고지와 라이선스 사본의 보존을 요구합니다. 라이선스 전문은 다음에서
확인할 수 있습니다:

    http://www.apache.org/licenses/LICENSE-2.0

Apache License 2.0 요약 (전문은 위 URL 참조):

```
Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

각 프로젝트의 원본 라이선스·NOTICE 파일:

- Serilog: https://github.com/serilog/serilog/blob/main/LICENSE
- AlphaVSS: https://github.com/alphaleonis/AlphaVSS/blob/master/LICENSE.md

---

## Microsoft Visual C++ Runtime (동봉)

배포본에는 Microsoft Visual C++ 재배포 가능 런타임의 다음 파일이 포함됩니다:

- `vcruntime140.dll`
- `vcruntime140_1.dll`

**포함 이유**: VSS(볼륨 섀도 복사본) 기능이 쓰는 `AlphaVSS.x64.dll`은 C++/CLI 혼합
어셈블리라 `vcruntime140.dll`을 필요로 합니다. 이 파일이 없는 PC에서는 VSS가 로드되지
않아 "실행 중인 시스템 디스크 클론"이 불가능해집니다. 사용자가 별도로 재배포 패키지를
설치하지 않아도 되도록 앱과 함께 배포합니다(app-local 배포).

**출처**: Visual Studio에 포함된 재배포 파일
(`VC\Redist\MSVC\<버전>\x64\Microsoft.VC145.CRT`).

**라이선스**: Microsoft Visual Studio의 배포 가능 코드(Distributable Code) 조항에 따라
재배포가 허용된 파일입니다. 저작권은 Microsoft Corporation에 있습니다.
https://visualstudio.microsoft.com/license-terms/
