@echo off
setlocal

REM ===== CONFIG =====
set REPO_URL=https://github.com/beinforit/MoonOutsideMapTablet.git
set COMMIT_MSG=Auto update
REM ==================

if not exist .git (
    echo Initializing git repository...
    git init
    git remote add origin %REPO_URL%
    git branch -M main
)

echo Adding files...
git add .

echo Committing...
git commit -m "%COMMIT_MSG%" || echo Nothing to commit

echo Pushing to GitHub...
git push origin main

echo Done.
pause
