<?php
use Illuminate\Support\Facades\Schedule;
Schedule::command('reports:send')->daily();
