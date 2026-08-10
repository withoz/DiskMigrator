using DiskMigrator.Bridge;

// Claude 데스크톱 앱이 이 실행 파일을 켜고 표준입출력으로 이야기합니다.
// 표준입력이 닫히면(=Claude가 종료하면) 중계도 끝납니다.
//
// 인자를 요구하지 않습니다 — 이 실행 파일은 중계 말고 하는 일이 없습니다. 앱이 설정에
// 적어 두는 --mcp-stdio 인자는 받아도 그만인 표시로 남겨 둡니다(사람이 설정 파일을 봤을 때
// 무엇을 하는 프로그램인지 알아볼 수 있게).
return await StdioBridge.RunAsync();
