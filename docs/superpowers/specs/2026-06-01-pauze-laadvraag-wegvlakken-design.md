# Pauze-Laadvraag Wegvlakken Design

## Doel

De tool krijgt een aparte analyse voor publieke laadinfrastructuur langs snelwegen: per wegvlak willen we zien hoeveel laadvermogen gevraagd wordt door voertuigen die rond hun wettelijke pauzemoment langs dat wegvlak komen. Dit is nadrukkelijk iets anders dan algemene wegdrukte of stilstandlocatie-laadvraag.

## Kernregels

- Default pauzevenster: `3,5` tot `4,5` uur rijtijd sinds start van de huidige shift.
- Default pauzeduur/laadduur: `0,75` uur.
- Default shift-reset gap: `2,0` uur.
- Laden/lossen, klantbezoek en gewone operationele stops tellen niet als pauze en resetten de rijtijdklok niet.
- Een nieuwe shift wordt alleen herkend bij een gap tussen ritten van minimaal de reset-gap en nabij een resetlocatie.
- Resetlocaties zijn bekende PostNL-standplaatsen plus automatisch herkende vaste start-/eindlocaties uit ritdata.
- Energie wordt berekend vanaf de start van de huidige shift, niet vanaf de start van alleen de losse rit.

## Gebruikersinstellingen

In het linkerpaneel komt een apart blok `Pauze-laadvraag snelweg`.

Velden:

- `Rijtijd vanaf (uur)`: default `3,5`.
- `Rijtijd tot (uur)`: default `4,5`.
- `Pauzeduur (uur)`: default `0,75`.
- `Shift-reset gap (uur)`: default `2,0`.
- `Resetlocaties`: vaste tekst of read-only status: `Standplaatsen + vaste start/eindlocaties`.

De bestaande voertuigparameter `kWh/km` blijft leidend voor de energievraag.

## Shiftlogica

De berekening loopt per voertuig chronologisch door ritten heen.

Per voertuig:

1. Sorteer alle ritten op starttijd.
2. Start een nieuwe shift bij de eerste rit.
3. Voor elke volgende rit:
   - Bereken de gap tussen vorige `trip_end` en huidige `trip_start`.
   - Reset de shift alleen als `gap >= shiftResetHours` en de vorige eindlocatie of huidige startlocatie nabij een resetlocatie ligt.
   - Als die voorwaarden niet gelden, loopt dezelfde shift door.
4. Binnen een shift worden rijtijd en afstand cumulatief opgebouwd.

Rijtijd is in de eerste versie gebaseerd op effectieve ritduur tussen `trip_start` en `trip_end`, exclusief gaps tussen ritten. Operationele stops binnen een rit blijven onderdeel van de ritduur omdat de brondata geen betrouwbare wettelijke pauzeclassificatie geeft. We noemen dit daarom in de UI `geschatte rijtijd`.

## Resetlocaties

Resetlocaties bestaan uit twee bronnen.

1. Bekende PostNL-standplaatsen uit het wagenparkbestand.
2. Automatisch herkende vaste start-/eindlocaties uit ritdata.

Automatische resetlocaties worden alleen opgenomen als ze voldoende structureel zijn:

- minimaal `5` unieke voertuigen, of
- minimaal `20` starts/einden in de gefilterde dataset.

De matchradius voor resetlocaties is `0,75 km`. Dit is ruim genoeg voor terreinen en adressen die afgerond/geocodeerd zijn, maar beperkt genoeg om gewone klantlocaties niet snel als depot te behandelen.

## Wegvlakmatching

Voor elke rit binnen een shift bepalen we of het pauzevenster binnen de cumulatieve rijtijd van die rit valt.

Voor een rit met:

- cumulatieve rijtijd bij ritstart,
- cumulatieve rijtijd bij riteinde,
- cumulatieve afstand bij ritstart,
- ritafstand,
- ritduur,

berekenen we het deel van het venster `3,5-4,5 uur` dat in deze rit valt. Voor dat interval bepalen we de afstandsprogressie op basis van lineaire voortgang over de ritafstand.

De voorkeursmatching gebruikt beschikbare route-/wegvlakdata:

1. Als routegeometrie of route-segmentkoppeling per rit beschikbaar is, gebruiken we progressie langs die route.
2. Als alleen start/eind en ritafstand beschikbaar zijn, gebruiken we een conservatieve benadering op basis van de bestaande wegvlakindex en markeren we de betrouwbaarheid lager.

De eerste implementatie moet zichtbaar maken hoeveel records met hoge of lage routekwaliteit zijn meegenomen. Als routekwaliteit onvoldoende is, mag de kaartlaag niet doen alsof de uitkomst exact is.

## Vermogensberekening

Per voertuigpassage in het pauzevenster:

```text
afstandSindsShiftStartKm * kWhPerKm = verbruikteKWh
verbruikteKWh / pauzeduurUur = gevraagdKw
```

Voorbeeld:

```text
100 kWh / 0,75 uur = 133,33 kW
```

De berekening gebruikt dus de verbruikte energie sinds shiftstart. Dat voorkomt onderschatting bij voertuigen die meerdere ritten in dezelfde shift rijden.

## Gelijktijdigheid

De analyse bouwt twee aggregaties.

- Detailniveau: kwartierblokken.
- Kaartniveau: hoogste uurpiek per wegvlak.

Per kwartierblok tellen we de gevraagde kW op van voertuigen waarvan het verwachte pauze-/laadmoment in dat blok valt. De verwachte laadsessie duurt standaard `45` minuten en bezet dus drie kwartierblokken.

Voor de kaart wordt per wegvlak de hoogste uurpiek getoond, zodat de kaart scanbaar blijft. In het detailpaneel tonen we het kwartierprofiel om de echte gelijktijdigheid te zien.

## UI-gedrag

Er komt een nieuwe kaartlaag:

`Pauze-laadvraag wegvlakken`

Bij inschakelen toont de kaart wegvlakken met pauze-laadvraag. De stijl schaalt op piekvraag in MW of kW. Bij klikken op een wegvlak verschijnt direct onder de kaart een detailpaneel met:

- Piekvraag in MW.
- Totaal gevraagde kWh.
- Aantal voertuigen in pauzevenster.
- Aantal voertuigpassages.
- Hoogste uur.
- Kwartierprofiel.
- Tabel per kenteken/voertuig:
  - kenteken,
  - voertuigcode,
  - starttijd shift,
  - passage-/pauzetijd,
  - rijtijd sinds shiftstart,
  - km sinds shiftstart,
  - kWh-vraag,
  - gevraagd kW,
  - routekwaliteit.

De bestaande wegvlakdetails blijven bestaan. Deze nieuwe laag is een aparte analyse en vervangt de huidige passagetelling niet.

## Sanity checks

Records worden uitgesloten of als lage kwaliteit gemarkeerd bij:

- ontbrekende start- of eindtijd,
- ritduur kleiner dan of gelijk aan `0`,
- ritafstand kleiner dan of gelijk aan `0`,
- ontbrekende start- of eindlocatie,
- gemiddelde snelheid onder `5 km/u` of boven `95 km/u`,
- pauzevenster valt buiten de cumulatieve rijtijd van de shift,
- route-/wegvlakmatch ontbreekt.

De UI toont datakwaliteit:

- aantal meegenomen passages,
- aantal uitgesloten ritten,
- aantal lage-kwaliteit matches,
- belangrijkste uitsluitredenen.

## API en datamodel

Nieuwe request:

`RoadBreakDemandRequest`

Velden:

- bestaande `AnalysisFilter` velden,
- `KwhPerKm`,
- `WindowStartHours`,
- `WindowEndHours`,
- `BreakDurationHours`,
- `ShiftResetGapHours`,
- `ResetLocationRadiusKm`.

Nieuwe responses:

- `RoadBreakDemandMapResponse`: kaartlijnen met piek-MW, voertuigen, kWh en routekwaliteit.
- `RoadBreakDemandDetailResponse`: detail voor een geselecteerd wegvlak met kwartierprofiel en voertuigregels.

## Teststrategie

Servicetests bouwen synthetische ritten met meerdere ritten per shift en meerdere shifts per dag.

Tests:

- Een voertuig met twee ritten zonder reset telt rijtijd en afstand door over beide ritten.
- Een gap van minimaal `2 uur` bij een resetlocatie start een nieuwe shift.
- Een lange gap buiten resetlocaties reset niet.
- Laden/lossenachtige stops resetten niet.
- Alleen voertuigen binnen `3,5-4,5` uur rijtijd tellen mee.
- kWh en kW volgen `afstandSindsShiftStart * kWhPerKm / 0,75`.
- Kwartierprofiel telt gelijktijdige voertuigen op.
- Kaartresponse toont de hoogste uurpiek.
- Onmogelijke ritten worden uitgesloten of als lage kwaliteit geteld.

## Scope eerste implementatie

De eerste implementatie levert:

- nieuwe instellingen in het linkerpaneel,
- nieuwe kaartlaag voor pauze-laadvraag,
- nieuwe API/serviceberekening,
- detailpaneel bij wegvlakklik,
- datakwaliteitssamenvatting,
- tests voor shiftlogica, vermogensberekening en aggregatie.

Niet in scope voor de eerste implementatie:

- optimalisatieadvies voor exacte laadpleinlocaties,
- reserveringslogica,
- charger-capacity matching,
- juridische tachograafvalidatie.
