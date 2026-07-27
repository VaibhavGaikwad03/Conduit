// Process-wide COM object/lock count so DllCanUnloadNow knows when it's safe to
// unload. Every COM object bumps this on construction and drops it on destruction.
#pragma once

void ModuleAddRef();
void ModuleRelease();
long ModuleLockCount();
